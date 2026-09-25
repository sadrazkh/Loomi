// Loomi API v1 with Node 18+ fetch: start a chat, wait for its picture, download it, then continue the chat.
// Run: LOOMI_TOKEN=lm_... node loomi.mjs
import { writeFile } from 'node:fs/promises';

const BASE = 'https://loomi.example.com';
const AUTH = { Authorization: `Bearer ${process.env.LOOMI_TOKEN}` };
const sleep = seconds => new Promise(resolve => setTimeout(resolve, seconds * 1000));

// One request. Waits out a 429 as Retry-After asks, and throws with the error code on any other failure.
async function call(method, path, body) {
  for (;;) {
    const response = await fetch(BASE + path, {
      method,
      headers: body ? { ...AUTH, 'Content-Type': 'application/json' } : AUTH,
      body: body && JSON.stringify(body),
      signal: AbortSignal.timeout(90_000),
    });
    if (response.status === 429) { await sleep(Number(response.headers.get('Retry-After') ?? 60)); continue; }
    if (!response.ok) throw new Error(`HTTP ${response.status} ${await response.text()}`);
    return response;
  }
}

// Long-polls until the message is finished; each call holds for up to a minute.
async function waitFor(id) {
  for (;;) {
    const { done, message } = await (await call('GET', `/api/v1/messages/${id}/wait?timeout=60`)).json();
    if (done) return message;
    console.log('still', message.stage);
  }
}

// Downloads a finished message's picture, or says why there is none.
async function save(message, file) {
  if (message.status !== 'completed') throw new Error(`no picture: ${message.status} ${message.error ?? message.text}`);
  const image = await call('GET', message.imageUrl);
  await writeFile(file, Buffer.from(await image.arrayBuffer()));
  console.log('saved', file);
}

// 1. Start a chat with its first message.
const started = await (await call('POST', '/api/v1/chats', { prompt: 'a lighthouse at dusk, film grain' })).json();
// 2. Wait for the answer and download the picture.
await save(await waitFor(started.message.id), 'lighthouse.png');
// 3. The next message in the same chat, then wait and download again.
const sent = await (await call('POST', `/api/v1/chats/${started.chat.id}/messages`, { prompt: 'the same scene in winter' })).json();
await save(await waitFor(sent.message.id), 'lighthouse-winter.png');
