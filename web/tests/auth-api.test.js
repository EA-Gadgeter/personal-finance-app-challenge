import assert from 'node:assert/strict';
import { test } from 'node:test';
import { getAccount, submitAuth } from '../src/auth-api.js';

test('signup fetches an antiforgery token before posting JSON', async (t) => {
  const credentials = { email: 'user@example.com', password: 'test-password' };
  const signal = new AbortController().signal;
  const signupResponse = new Response(null, { status: 201 });
  const fetch = t.mock.method(globalThis, 'fetch', async (path) => {
    if (path === '/api/auth/csrf') {
      return Response.json({ requestToken: 'test-token' });
    }
    return signupResponse;
  });

  assert.equal(await submitAuth('signup', credentials, signal), signupResponse);
  assert.deepEqual(fetch.mock.calls.map(({ arguments: args }) => args), [
    ['/api/auth/csrf', { signal }],
    ['/api/auth/signup', {
      method: 'POST',
      headers: {
        'X-CSRF-Token': 'test-token',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(credentials),
      signal,
    }],
  ]);
});

test('a failed antiforgery request prevents the auth POST', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => new Response(null, { status: 503 }));

  await assert.rejects(submitAuth('logout'), /Could not start the request/);
  assert.equal(fetch.mock.callCount(), 1);
  assert.equal(fetch.mock.calls[0].arguments[0], '/api/auth/csrf');
});

test('sign-out posts the antiforgery token with an empty body', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async (path) =>
    path === '/api/auth/csrf'
      ? Response.json({ requestToken: 'test-token' })
      : new Response(null, { status: 204 }));

  await submitAuth('logout');
  assert.deepEqual(fetch.mock.calls[1].arguments, [
    '/api/auth/logout', {
      method: 'POST',
      headers: { 'X-CSRF-Token': 'test-token' },
      body: undefined,
      signal: undefined,
    },
  ]);
});

test('account lookup forwards the response so the UI can handle expiry', async (t) => {
  const expired = new Response(null, { status: 401 });
  const signal = new AbortController().signal;
  const fetch = t.mock.method(globalThis, 'fetch', async () => expired);

  assert.equal(await getAccount(signal), expired);
  assert.deepEqual(fetch.mock.calls[0].arguments, ['/api/auth/me', { signal }]);
});
