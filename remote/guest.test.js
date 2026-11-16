'use strict';
// The guest's page in French and in English. Run with `node --test` in this folder.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { words, language, reason, again } = require('./guest.js');

// gateway is the headers of the gateway's answer that refuses for why, or for no reason it names.
const gateway = (why) => new Headers(why === undefined ? { 'X-Delta-Gateway': '1' } : { 'X-Delta-Gateway': '1', 'X-Delta-Refusal': why });

// told is every key reason gives, for each refusal the gateway names, an unknown one, none, and no gateway at all.
const told = ['ended', 'revoked', 'window', 'full', 'relay', 'other', undefined]
  .flatMap((why) => ['used', 'noInvite', 'ended'].map((ended) => reason(gateway(why), ended)))
  .concat([reason(null, 'used'), reason(new Headers(), 'used')]);

test('the page speaks French to a French browser and English to every other', () => {
  assert.equal(language(['fr-CA', 'en']), 'fr');
  assert.equal(language(['en-US', 'fr']), 'en');
  assert.equal(language(['de-DE', 'fr-FR']), 'fr');
  assert.equal(language(['de-DE', 'es']), 'en');
  assert.equal(language(['FR']), 'fr');
  assert.equal(language([]), 'en');
  assert.equal(language(undefined), 'en');
});

test('both languages say everything the page says', () => {
  const said = new Set([...fs.readFileSync(path.join(__dirname, 'guest.js'), 'utf8').matchAll(/\bw\.(\w+)/g)].map((m) => m[1]).concat(told));
  assert.ok(said.size >= 10, `the page says only ${[...said]}`);
  for (const [lang, w] of Object.entries(words)) {
    assert.deepEqual(Object.keys(w).sort(), Object.keys(words.en).sort(), `${lang} has other words than English`);
    for (const key of said) assert.ok(typeof w[key] === 'string' && w[key].trim() !== '', `${lang} does not say ${key}`);
  }
});

// The tunnel answers alone while the gateway is stopped, and nothing answers while the guest is offline: the page
// says Delta is away and tries again, as it does while Delta's window is closed. An invitation ended or a time that is
// up does not pass by itself: the page says so and leaves it to the guest.
test('the page tells why it cannot watch, and tries again by itself only for what passes', () => {
  assert.equal(reason(null, 'used'), 'away');
  assert.equal(reason(new Headers(), 'used'), 'away');
  assert.equal(reason(new Headers({ 'X-Delta-Refusal': 'revoked' }), 'used'), 'away', 'a refusal the gateway did not give');
  assert.equal(reason(gateway('window'), 'used'), 'window');
  assert.equal(reason(gateway('revoked'), 'used'), 'revoked');
  assert.equal(reason(gateway('ended'), 'used'), 'used');
  assert.equal(reason(gateway('ended'), 'noInvite'), 'noInvite');
  assert.equal(reason(gateway('ended'), 'ended'), 'ended');
  assert.equal(reason(gateway('full'), 'ended'), 'full');
  assert.equal(reason(gateway('relay'), 'ended'), 'relay');
  assert.equal(reason(gateway('other'), 'ended'), 'refused');
  assert.equal(reason(gateway(), 'ended'), 'refused', 'a refusal without a reason, such as an offer that does not parse');
  assert.deepEqual([...again].sort(), ['away', 'window']);
  for (const key of new Set(told)) assert.equal(again.has(key), key === 'away' || key === 'window', key);
});
