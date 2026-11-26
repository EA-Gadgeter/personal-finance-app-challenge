const authPath = '/api/auth';

export async function submitAuth(action, body, signal) {
  const csrfResponse = await fetch(`${authPath}/csrf`, { signal });
  if (!csrfResponse.ok) {
    throw new Error('Could not start the request.');
  }

  const { requestToken } = await csrfResponse.json();
  const headers = { 'X-CSRF-Token': requestToken };
  if (body) {
    headers['Content-Type'] = 'application/json';
  }

  return fetch(`${authPath}/${action}`, {
    method: 'POST',
    headers,
    body: body ? JSON.stringify(body) : undefined,
    signal,
  });
}

export function getAccount(signal) {
  return fetch(`${authPath}/me`, { signal });
}
