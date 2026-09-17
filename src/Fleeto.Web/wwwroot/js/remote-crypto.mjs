// End-to-end encryption of remote sessions in the browser (0.3.0, ARCHITECTURE.md §4 Remote session). The Go endpoint implements the same
// in agent/internal/remote/crypto.go; tests/browser/remote-crypto.test.mjs checks both against agent/internal/remote/testdata/vectors.json.
//
// The browser's X25519 key is generated here and never leaves the page (not extractable). The token fleeto-signer issued binds its public
// half; the endpoint signs its own ephemeral key with its certificate key. Keys: HKDF-SHA256 over the X25519 secret, salt SHA-256 of the
// token payload, info "fleeto-remote-v1" || browser key || endpoint key, 64 bytes: browser to endpoint, then endpoint to browser. Frames:
// AES-256-GCM, nonce four zero bytes and a 64-bit big-endian counter per direction, never sent, so a changed, replayed, dropped or
// reordered frame fails and ends the session.

const encoder = new TextEncoder();
const subtle = globalThis.crypto.subtle;

export const EndpointKeyContext = "fleeto-remote-endpoint-key-v1";
export const KeyInfo = "fleeto-remote-v1";

/** Frame types: the first byte of every decrypted frame (agent/internal/remote/session.go). */
export const Frame = Object.freeze({
  Hello: 0x01,
  Open: 0x02,
  Opened: 0x03,
  Data: 0x04,
  Resize: 0x05,
  CloseChannel: 0x06,
  Closed: 0x07,
  IdleWarning: 0x08,
  End: 0x09,
  Activity: 0x0a,
  // Remote background operations (0.3.0 step 2).
  Request: 0x0b,
  Response: 0x0c,
  Chunk: 0x0d,
  Transfer: 0x0e
});

function concat(...parts) {
  const length = parts.reduce((total, part) => total + part.length, 0);
  const out = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

export function fromBase64(text) {
  const binary = atob(text);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}

export function toBase64(bytes) {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

/** True when this browser has everything a remote session needs. */
export async function supported() {
  try {
    const key = await subtle.generateKey({ name: "X25519" }, false, ["deriveBits"]);
    return Boolean(key && key.publicKey);
  } catch {
    return false;
  }
}

/** A fresh ephemeral X25519 key pair; the private key cannot be exported. */
export async function generateBrowserKey() {
  const pair = await subtle.generateKey({ name: "X25519" }, false, ["deriveBits"]);
  const publicKey = new Uint8Array(await subtle.exportKey("raw", pair.publicKey));
  return { privateKey: pair.privateKey, publicKey };
}

function hex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * Accepts the certificate key the endpoint sent (SPKI, DER) only when its SHA-256 is one of the public key fingerprints web gave for the
 * serving service, verifies the endpoint's signature over its session key with it, then derives the two directional keys. Throws otherwise.
 */
export async function deriveSessionKeys(browserPrivateKey, browserPublicKey, tokenPayload, endpointPublicKey, signature, certificatePublicKey, fingerprints) {
  if (endpointPublicKey.length !== 32 || endpointPublicKey.every((b) => b === 0) || signature.length !== 64) {
    throw new Error("The endpoint sent a malformed session key.");
  }
  const fingerprint = hex(new Uint8Array(await subtle.digest("SHA-256", certificatePublicKey)));
  if (!fingerprints.includes(fingerprint)) {
    throw new Error("The endpoint's certificate is not one this instance issued. The session was not started.");
  }
  const payloadHash = new Uint8Array(await subtle.digest("SHA-256", tokenPayload));
  const message = concat(encoder.encode(EndpointKeyContext), new Uint8Array([0]), payloadHash, endpointPublicKey);
  const key = await subtle.importKey("spki", certificatePublicKey, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  if (!(await subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, signature, message))) {
    throw new Error("The session key is not signed by this endpoint. The session was not started.");
  }
  const endpointKey = await subtle.importKey("raw", endpointPublicKey, { name: "X25519" }, false, []);
  const shared = new Uint8Array(await subtle.deriveBits({ name: "X25519", public: endpointKey }, browserPrivateKey, 256));
  const hkdfKey = await subtle.importKey("raw", shared, "HKDF", false, ["deriveBits"]);
  const info = concat(encoder.encode(KeyInfo), browserPublicKey, endpointPublicKey);
  const bits = new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt: payloadHash, info }, hkdfKey, 512));
  return { browserToEndpoint: bits.slice(0, 32), endpointToBrowser: bits.slice(32, 64) };
}

/**
 * One direction of a session. Frames must be sealed and opened in order; the counter is taken synchronously when a call starts, so
 * concurrent calls keep their order. After one failed open every later open fails.
 */
export class FrameCipher {
  static async create(rawKey) {
    const key = await subtle.importKey("raw", rawKey, "AES-GCM", false, ["encrypt", "decrypt"]);
    return new FrameCipher(key);
  }

  constructor(key) {
    this.key = key;
    this.counter = 0n;
    this.failed = false;
  }

  nextNonce() {
    const nonce = new Uint8Array(12);
    new DataView(nonce.buffer).setBigUint64(4, this.counter);
    this.counter++;
    return nonce;
  }

  async seal(plaintext) {
    const iv = this.nextNonce();
    return new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv }, this.key, plaintext));
  }

  async open(frame) {
    if (this.failed || frame.length < 16) {
      this.failed = true;
      throw new Error("A session frame failed authentication.");
    }
    const iv = this.nextNonce();
    try {
      return new Uint8Array(await subtle.decrypt({ name: "AES-GCM", iv }, this.key, frame));
    } catch {
      this.failed = true;
      throw new Error("A session frame failed authentication.");
    }
  }
}
