'use strict';
// Hands each 128-sample block of the microphone to the page, which keeps them while the talk button is held.
class Recorder extends AudioWorkletProcessor {
  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (channel) this.port.postMessage(channel.slice(0));
    return true;
  }
}
registerProcessor('recorder', Recorder);
