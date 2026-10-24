'use strict';
// The guest's page in French and in English. Run with `node --test` in this folder.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { words, language } = require('./guest.js');

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
  const said = new Set([...fs.readFileSync(path.join(__dirname, 'guest.js'), 'utf8').matchAll(/\bw\.(\w+)/g)].map((m) => m[1]));
  assert.ok(said.size >= 10, `the page says only ${[...said]}`);
  for (const [lang, w] of Object.entries(words)) {
    assert.deepEqual(Object.keys(w).sort(), Object.keys(words.en).sort(), `${lang} has other words than English`);
    for (const key of said) assert.ok(typeof w[key] === 'string' && w[key].trim() !== '', `${lang} does not say ${key}`);
  }
});
