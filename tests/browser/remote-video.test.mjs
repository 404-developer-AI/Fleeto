// Checks the H.264 path of the remote control viewer (src/Fleeto.Web/wwwroot/js/remote-video.mjs, 0.3.0 step 5): a FrameVideo the Go
// endpoint produced (agent/internal/screen VideoVector) is parsed, joined and decoded with the codec its SPS names; the frame is drawn and
// acknowledged only once the decoder gave the picture; a delta frame before any key frame is acknowledged without decoding; a decoder error
// is reported. WebCodecs is replaced by a fake. Run: node --test tests/browser/remote-video.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { codecFromSps, parseVideo, ScreenStats, VideoStream } from "../../src/Fleeto.Web/wwwroot/js/remote-video.mjs";

const VIDEO_B64 = "HwAAAAcDB38EOAAALuAAAAu4AAAAAWdNQCiqAAAAAWjuAAAAAWWIhA==";
const body = () => Uint8Array.from(Buffer.from(VIDEO_B64, "base64")).subarray(1); // the session strips the type byte

function installWebCodecs() {
  const decoders = [];
  globalThis.EncodedVideoChunk = class {
    constructor(init) {
      Object.assign(this, init);
    }
  };
  globalThis.VideoDecoder = class {
    constructor(init) {
      this.init = init;
      this.state = "unconfigured";
      this.chunks = [];
      decoders.push(this);
    }
    configure(config) {
      this.config = config;
      this.state = "configured";
    }
    decode(chunk) {
      this.chunks.push(chunk);
    }
    close() {
      this.state = "closed";
    }
    // output hands the picture of a decoded chunk back, as a real decoder does later.
    output(chunk) {
      this.init.output({ timestamp: chunk.timestamp, closed: false, close() { this.closed = true; } });
    }
  };
  return decoders;
}

function stream(overrides = {}) {
  const events = { drawn: [], acks: [], failures: [] };
  const video = new VideoStream({
    draw: (frame) => events.drawn.push(frame.timestamp),
    ack: (frame) => events.acks.push(frame),
    failed: (message) => events.failures.push(message),
    stats: new ScreenStats(),
    lastAckAt: () => null,
    ...overrides
  });
  return { video, events };
}

test("the header of a FrameVideo is read as the endpoint wrote it", () => {
  const v = parseVideo(body());
  assert.equal(v.frame, 7);
  assert.equal(v.key, true);
  assert.equal(v.last, true);
  assert.equal(v.width, 1919);
  assert.equal(v.height, 1080);
  assert.equal(v.endpointMs, 12);
  assert.equal(v.waitedMs, 3);
  assert.equal(codecFromSps(v.data), "avc1.4d4028");
  assert.equal(parseVideo(new Uint8Array(5)), null);
});

test("a key frame configures the decoder from its SPS and is acknowledged after it is drawn", () => {
  const decoders = installWebCodecs();
  const { video, events } = stream();
  video.push(body());
  assert.equal(decoders.length, 1);
  const decoder = decoders[0];
  assert.deepEqual(decoder.config, { codec: "avc1.4d4028", codedWidth: 1920, codedHeight: 1080, optimizeForLatency: true });
  assert.equal(decoder.chunks.length, 1);
  assert.equal(decoder.chunks[0].type, "key");
  assert.equal(decoder.chunks[0].timestamp, 7);
  assert.deepEqual([...decoder.chunks[0].data.subarray(0, 5)], [0, 0, 0, 1, 0x67]);
  assert.deepEqual(events.acks, [], "nothing is acknowledged before the picture is drawn");

  decoder.output(decoder.chunks[0]);
  assert.deepEqual(events.drawn, [7]);
  assert.deepEqual(events.acks, [7]);
});

test("the parts of a frame are joined before decoding", () => {
  const decoders = installWebCodecs();
  const { video } = stream();
  const whole = body();
  const first = whole.slice();
  first[4] &= ~1; // not the last part
  first.set([0, 0, 0, 1, 0x67, 0x4d, 0x40], 17);
  const firstPart = first.subarray(0, 17 + 7);
  const second = whole.slice(0, 17);
  const rest = whole.subarray(17 + 7);
  const secondPart = new Uint8Array(17 + rest.length);
  secondPart.set(second, 0);
  secondPart.set(rest, 17);
  video.push(firstPart);
  assert.equal(decoders.length, 0, "nothing is decoded before the last part");
  video.push(secondPart);
  assert.deepEqual([...decoders[0].chunks[0].data], [...whole.subarray(17)]);
});

test("a delta frame before any key frame is acknowledged without decoding", () => {
  const decoders = installWebCodecs();
  const { video, events } = stream();
  const delta = body().slice();
  delta[3] = 8; // frame 8
  delta[4] = 1; // last, not a key frame
  video.push(delta);
  assert.equal(decoders.length, 0);
  assert.deepEqual(events.acks, [8]);
});

test("a decoder error is reported and the next key frame starts a new decoder", () => {
  const decoders = installWebCodecs();
  const { video, events } = stream();
  video.push(body());
  decoders[0].state = "closed";
  decoders[0].init.error(new Error("decoding failed"));
  assert.deepEqual(events.failures, ["decoding failed"]);
  video.push(body());
  assert.equal(decoders.length, 2, "a closed decoder is replaced");
});

test("the latency estimate adds the endpoint, half the round trip, the transfer and the decoding", () => {
  const decoders = installWebCodecs();
  const stats = new ScreenStats();
  let clock = 1000;
  globalThis.performance = { now: () => clock };
  const { video } = stream({ stats, lastAckAt: () => 900 });
  video.push(body()); // arrives at 1000: 100 ms after the previous ack, of which 12 + 3 on the endpoint
  clock = 1010;
  decoders[0].output(decoders[0].chunks[0]);
  const summary = stats.summary();
  // endpoint 12 + (100 - 3 - 12) / 2 + transfer 0 + decoding 10 = 64.5
  assert.equal(summary.latencyMs, 65);
  assert.equal(summary.breakdown.endpoint, 12);
});
