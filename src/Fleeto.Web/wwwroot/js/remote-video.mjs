// H.264 in the remote control window (0.3.0 step 5): the endpoint sends each frame as one H.264 access unit (Annex B), cut into
// FrameVideo parts (agent/internal/screen/video.go). This joins the parts, decodes them with WebCodecs and draws each picture on the canvas,
// then acknowledges the frame so the endpoint captures the next. It also keeps the statistics the window shows: frames a second, bit rate
// and an estimate of the latency from the timings the endpoint puts in every frame.

/** The header after the type byte: frame uint32 | flags uint8 | width uint16 | height uint16 | endpoint uint32 | waited uint32 (µs). */
export const VideoHeaderBytes = 17;
const FlagLast = 1;
const FlagKey = 2;

/** The most one frame may take, in bytes and in parts; more is not a frame of a screen and is dropped. */
const maxFrameBytes = 32 * 1024 * 1024;
const maxFrameParts = 64;

/** The codec name the endpoint knows (agent/internal/screen CodecH264). */
export const CodecH264 = "h264";

/** The codec string decodableCodecs checks: H.264 Main profile, level 5.1 (up to 4096 x 2304), as the endpoint encodes. */
const probeCodec = "avc1.4d0033";

/**
 * The codecs this browser decodes besides tiles, for FrameStart: ["h264"] where WebCodecs decodes H.264, else none.
 * @returns {Promise<string[]>}
 */
export async function decodableCodecs() {
  if (typeof VideoDecoder !== "function" || typeof EncodedVideoChunk !== "function") {
    return [];
  }
  try {
    const result = await VideoDecoder.isConfigSupported({ codec: probeCodec, optimizeForLatency: true });
    return result?.supported ? [CodecH264] : [];
  } catch {
    return [];
  }
}

/** parseVideo reads the header of a FrameVideo body (the type byte stripped). */
export function parseVideo(body) {
  if (body.length < VideoHeaderBytes) {
    return null;
  }
  const view = new DataView(body.buffer, body.byteOffset, body.length);
  const flags = view.getUint8(4);
  return {
    frame: view.getUint32(0),
    last: (flags & FlagLast) !== 0,
    key: (flags & FlagKey) !== 0,
    width: view.getUint16(5),
    height: view.getUint16(7),
    endpointMs: view.getUint32(9) / 1000,
    waitedMs: view.getUint32(13) / 1000,
    data: body.subarray(VideoHeaderBytes)
  };
}

/** codecFromSps returns the codec string (avc1.PPCCLL) from the SPS in an access unit, or null when it has none. */
export function codecFromSps(unit) {
  for (let i = 0; i + 6 < unit.length; i++) {
    if (unit[i] === 0 && unit[i + 1] === 0 && unit[i + 2] === 1 && (unit[i + 3] & 0x1f) === 7) {
      const hex = (b) => b.toString(16).padStart(2, "0");
      return `avc1.${hex(unit[i + 4])}${hex(unit[i + 5])}${hex(unit[i + 6])}`;
    }
  }
  return null;
}

function now() {
  return typeof performance !== "undefined" ? performance.now() : Date.now();
}

/** Statistics of the screen stream: frames drawn and bytes received in the last seconds, and the latency estimate. */
export class ScreenStats {
  constructor(clock = now) {
    this.clock = clock;
    this.samples = [];
    this.latencies = [];
    this.breakdown = null;
  }

  /** received counts the bytes of a frame part. */
  received(bytes) {
    const t = this.clock();
    this.samples.push({ t, bytes, drawn: 0 });
    this.trim(t);
  }

  /** drawn counts a frame on the screen. */
  drawn() {
    const t = this.clock();
    this.samples.push({ t, bytes: 0, drawn: 1 });
    this.trim(t);
  }

  /** reset forgets the latency, for a new codec or a new session. */
  reset() {
    this.latencies = [];
    this.breakdown = null;
  }

  latency(total, parts) {
    this.latencies.push(total);
    if (this.latencies.length > 20) {
      this.latencies.shift();
    }
    this.breakdown = parts;
  }

  trim(t) {
    while (this.samples.length > 0 && t - this.samples[0].t > 2000) {
      this.samples.shift();
    }
  }

  /** The numbers of the last two seconds: frames a second, bits a second and the median latency (null when unknown). */
  summary() {
    const t = this.clock();
    this.trim(t);
    const seconds = 2;
    const bytes = this.samples.reduce((sum, s) => sum + s.bytes, 0);
    const frames = this.samples.reduce((sum, s) => sum + s.drawn, 0);
    const sorted = [...this.latencies].sort((a, b) => a - b);
    return {
      fps: Math.round(frames / seconds),
      bitsPerSecond: Math.round(bytes * 8 / seconds),
      latencyMs: sorted.length > 0 ? Math.round(sorted[Math.floor(sorted.length / 2)]) : null,
      breakdown: this.breakdown
    };
  }
}

/**
 * VideoStream decodes the H.264 frames of one remote control session onto a canvas.
 * options: draw(videoFrame), ack(frameNumber), failed(message), stats (ScreenStats), lastAckAt() → when the last acknowledgement left.
 */
export class VideoStream {
  constructor(options) {
    this.options = options;
    this.decoder = null;
    this.config = null;
    this.parts = null;
    // Frames sent to the decoder, by number: when their parts arrived, for the latency.
    this.inFlight = new Map();
    this.closed = false;
  }

  /** push takes one FrameVideo body; the frame is decoded once its last part is there. */
  push(body) {
    const part = parseVideo(body);
    if (!part || this.closed) {
      return;
    }
    const t = now();
    if (!this.parts || this.parts.header.frame !== part.frame) {
      // A new frame; the parts of an unfinished one are dropped (they never come after a newer frame).
      this.parts = { header: part, chunks: [], bytes: 0, firstAt: t, previousAck: this.options.lastAckAt?.() ?? null };
    }
    this.parts.chunks.push(part.data);
    this.parts.bytes += body.length + 1;
    if (this.parts.bytes > maxFrameBytes || this.parts.chunks.length > maxFrameParts) {
      this.parts = null;
      this.options.failed("A video frame of the endpoint is larger than a screen can be.");
      return;
    }
    this.options.stats?.received(body.length + 1);
    if (!part.last) {
      return;
    }
    const whole = this.parts;
    this.parts = null;
    this.decode(whole, t);
  }

  decode(whole, lastAt) {
    const header = whole.header;
    const unit = join(whole.chunks);
    if (header.key) {
      const codec = codecFromSps(unit);
      if (!codec) {
        this.options.failed("A key frame arrived without its sequence header.");
        return;
      }
      if (!this.ensureDecoder(codec, header.width, header.height)) {
        return;
      }
    } else if (!this.decoder || this.decoder.state !== "configured") {
      // A picture that refers to one this decoder never saw (a technician who joined, a decoder that was reset): skip it and wait for
      // the key frame the endpoint sends.
      this.options.ack(header.frame);
      return;
    }
    this.inFlight.set(header.frame, { header, firstAt: whole.firstAt, lastAt, previousAck: whole.previousAck });
    try {
      this.decoder.decode(new EncodedVideoChunk({ type: header.key ? "key" : "delta", timestamp: header.frame, data: unit }));
    } catch (error) {
      this.inFlight.delete(header.frame);
      this.options.failed(error?.message ?? String(error));
    }
  }

  ensureDecoder(codec, width, height) {
    const config = { codec, codedWidth: width + (width & 1), codedHeight: height + (height & 1), optimizeForLatency: true };
    if (this.decoder && this.decoder.state === "configured" && this.config && this.config.codec === codec &&
        this.config.codedWidth === config.codedWidth && this.config.codedHeight === config.codedHeight) {
      return true;
    }
    try {
      if (!this.decoder || this.decoder.state === "closed") {
        this.decoder = new VideoDecoder({
          output: (frame) => this.onOutput(frame),
          error: (error) => this.onError(error)
        });
      }
      this.decoder.configure(config);
      this.config = config;
      return true;
    } catch (error) {
      this.options.failed(error?.message ?? String(error));
      return false;
    }
  }

  onOutput(frame) {
    const number = frame.timestamp;
    const drawnAt = now();
    try {
      if (!this.closed) {
        this.options.draw(frame);
      }
    } finally {
      frame.close();
    }
    const info = this.inFlight.get(number);
    this.inFlight.delete(number);
    if (this.closed) {
      return;
    }
    this.options.ack(number);
    if (info) {
      this.measure(info, drawnAt);
    }
  }

  // measure estimates the latency of a frame: the endpoint's capture and encoding, half the round trip (from the previous
  // acknowledgement to the first part of this frame, less what the endpoint spent and waited), the transfer and the decoding.
  measure(info, drawnAt) {
    const stats = this.options.stats;
    if (!stats) {
      return;
    }
    stats.drawn();
    if (info.previousAck === null) {
      return;
    }
    const roundTrip = info.firstAt - info.previousAck - info.header.waitedMs - info.header.endpointMs;
    if (roundTrip < 0 || roundTrip > 10000) {
      return;
    }
    const transfer = info.lastAt - info.firstAt;
    const decode = drawnAt - info.lastAt;
    const total = info.header.endpointMs + roundTrip / 2 + transfer + decode;
    stats.latency(total, { endpoint: info.header.endpointMs, network: roundTrip / 2 + transfer, browser: decode });
  }

  onError(error) {
    this.inFlight.clear();
    this.config = null;
    if (!this.closed) {
      this.options.failed(error?.message ?? String(error));
    }
  }

  /** reset forgets the decoder, so the next key frame starts a new one. */
  reset() {
    this.closeDecoder();
    this.parts = null;
  }

  closeDecoder() {
    if (this.decoder && this.decoder.state !== "closed") {
      try {
        this.decoder.close();
      } catch {
        // already closed by an error
      }
    }
    this.decoder = null;
    this.config = null;
    this.inFlight.clear();
  }

  close() {
    this.closed = true;
    this.closeDecoder();
    this.parts = null;
  }
}

function join(chunks) {
  if (chunks.length === 1) {
    return chunks[0].slice();
  }
  const out = new Uint8Array(chunks.reduce((sum, c) => sum + c.length, 0));
  let offset = 0;
  for (const chunk of chunks) {
    out.set(chunk, offset);
    offset += chunk.length;
  }
  return out;
}
