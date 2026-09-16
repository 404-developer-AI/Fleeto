// Checks the browser implementation of remote session encryption against the vectors the Go endpoint produced and verifies
// (agent/internal/remote/vectors_test.go), and that a changed, replayed or reordered frame fails. Run: node --test tests/browser/remote-crypto.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { deriveSessionKeys, FrameCipher, fromBase64, generateBrowserKey } from "../../src/Fleeto.Web/wwwroot/js/remote-crypto.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const vectors = JSON.parse(readFileSync(join(here, "../../agent/internal/remote/testdata/vectors.json"), "utf8"));

// WebCrypto imports an X25519 private key as PKCS#8 only.
function x25519Pkcs8(raw) {
  const prefix = Uint8Array.from([0x30, 0x2e, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x6e, 0x04, 0x22, 0x04, 0x20]);
  const out = new Uint8Array(prefix.length + raw.length);
  out.set(prefix);
  out.set(raw, prefix.length);
  return out;
}

async function browserKeys() {
  const privateKey = await crypto.subtle.importKey("pkcs8", x25519Pkcs8(fromBase64(vectors.browserPrivateKey)), { name: "X25519" }, false, ["deriveBits"]);
  return deriveSessionKeys(privateKey, fromBase64(vectors.browserPublicKey), fromBase64(vectors.tokenPayload), fromBase64(vectors.endpointPublicKey),
    fromBase64(vectors.endpointSignature), fromBase64(vectors.endpointCertificateSpki), [vectors.endpointCertificateFingerprint]);
}

test("derives the same keys as the Go endpoint", async () => {
  const keys = await browserKeys();
  assert.deepEqual(keys.browserToEndpoint, fromBase64(vectors.browserToEndpointKey));
  assert.deepEqual(keys.endpointToBrowser, fromBase64(vectors.endpointToBrowserKey));
});

test("seals and opens the same frames as the Go endpoint", async () => {
  const keys = await browserKeys();
  const seal = { browserToEndpoint: await FrameCipher.create(keys.browserToEndpoint), endpointToBrowser: await FrameCipher.create(keys.endpointToBrowser) };
  const open = { browserToEndpoint: await FrameCipher.create(keys.browserToEndpoint), endpointToBrowser: await FrameCipher.create(keys.endpointToBrowser) };
  for (const frame of vectors.frames) {
    assert.deepEqual(await seal[frame.direction].seal(fromBase64(frame.plaintext)), fromBase64(frame.ciphertext));
    assert.deepEqual(await open[frame.direction].open(fromBase64(frame.ciphertext)), fromBase64(frame.plaintext));
  }
});

test("refuses a certificate key the instance did not record", async () => {
  const privateKey = await crypto.subtle.importKey("pkcs8", x25519Pkcs8(fromBase64(vectors.browserPrivateKey)), { name: "X25519" }, false, ["deriveBits"]);
  const other = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const otherSpki = new Uint8Array(await crypto.subtle.exportKey("spki", other.publicKey));
  await assert.rejects(deriveSessionKeys(privateKey, fromBase64(vectors.browserPublicKey), fromBase64(vectors.tokenPayload), fromBase64(vectors.endpointPublicKey),
    fromBase64(vectors.endpointSignature), otherSpki, [vectors.endpointCertificateFingerprint]));
  await assert.rejects(deriveSessionKeys(privateKey, fromBase64(vectors.browserPublicKey), fromBase64(vectors.tokenPayload), fromBase64(vectors.endpointPublicKey),
    fromBase64(vectors.endpointSignature), fromBase64(vectors.endpointCertificateSpki), ["00"]));
});

test("refuses an endpoint key that the certificate key did not sign", async () => {
  const privateKey = await crypto.subtle.importKey("pkcs8", x25519Pkcs8(fromBase64(vectors.browserPrivateKey)), { name: "X25519" }, false, ["deriveBits"]);
  const swapped = (await generateBrowserKey()).publicKey;
  await assert.rejects(deriveSessionKeys(privateKey, fromBase64(vectors.browserPublicKey), fromBase64(vectors.tokenPayload), swapped,
    fromBase64(vectors.endpointSignature), fromBase64(vectors.endpointCertificateSpki), [vectors.endpointCertificateFingerprint]));
  const otherToken = fromBase64(vectors.tokenPayload);
  otherToken[otherToken.length - 1] ^= 1;
  await assert.rejects(deriveSessionKeys(privateKey, fromBase64(vectors.browserPublicKey), otherToken, fromBase64(vectors.endpointPublicKey),
    fromBase64(vectors.endpointSignature), fromBase64(vectors.endpointCertificateSpki), [vectors.endpointCertificateFingerprint]));
});

test("fails on a changed, replayed or reordered frame, and for good", async () => {
  const keys = await browserKeys();
  const endpointFrames = vectors.frames.filter((f) => f.direction === "endpointToBrowser").map((f) => fromBase64(f.ciphertext));

  const changed = await FrameCipher.create(keys.endpointToBrowser);
  const tampered = endpointFrames[0].slice();
  tampered[3] ^= 1;
  await assert.rejects(changed.open(tampered));
  await assert.rejects(changed.open(endpointFrames[0]));

  const replayed = await FrameCipher.create(keys.endpointToBrowser);
  await replayed.open(endpointFrames[0]);
  await assert.rejects(replayed.open(endpointFrames[0]));

  const reordered = await FrameCipher.create(keys.endpointToBrowser);
  await assert.rejects(reordered.open(endpointFrames[1]));

  // The browser's own frames reflected back never open with the other direction's key.
  const reflected = await FrameCipher.create(keys.endpointToBrowser);
  const own = vectors.frames.find((f) => f.direction === "browserToEndpoint");
  await assert.rejects(reflected.open(fromBase64(own.ciphertext)));
});
