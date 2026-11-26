import React, { useCallback, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { getAccount, submitAuth } from './auth-api.js';

function App() {
  const [location, setLocation] = useState(() => ({
    path: window.location.pathname,
    notice: '',
  }));
  const heading = useRef(null);

  useEffect(() => {
    function syncLocation() {
      setLocation({ path: window.location.pathname, notice: '' });
    }
    window.addEventListener('popstate', syncLocation);
    return () => window.removeEventListener('popstate', syncLocation);
  }, []);

  useEffect(() => {
    heading.current?.focus();
  }, [location.path]);

  const navigate = useCallback((path, { replace = false, notice = '' } = {}) => {
    const method = replace ? 'replaceState' : 'pushState';
    window.history[method](null, '', path);
    setLocation({ path, notice });
  }, []);

  const signedOut = useCallback((expired) => navigate('/login', {
    replace: true,
    notice: expired ? 'Your session ended. Sign in to continue.' : '',
  }), [navigate]);

  const { path, notice } = location;
  if (path === '/signup' || path === '/login') {
    const signup = path === '/signup';
    const otherPath = signup ? '/login' : '/signup';
    return (
      <main>
        <h1 ref={heading} tabIndex={-1}>{signup ? 'Create account' : 'Sign in'}</h1>
        {notice && <p role="status">{notice}</p>}
        <AuthForm
          key={path}
          signup={signup}
          onSuccess={() => signup
            ? navigate('/login', { replace: true, notice: 'Account created. Sign in to continue.' })
            : navigate('/', { replace: true })}
        />
        <p>
          <a href={otherPath} onClick={(event) => {
            if (event.button || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
              return;
            }
            event.preventDefault();
            navigate(otherPath);
          }}>
            {signup ? 'Sign in to an existing account' : 'Create an account'}
          </a>
        </p>
      </main>
    );
  }

  return <Account heading={heading} onSignedOut={signedOut} />;
}

function AuthForm({ signup, onSuccess }) {
  const [pending, setPending] = useState(false);
  const [fields, setFields] = useState({});
  const [message, setMessage] = useState('');
  const email = useRef(null);
  const password = useRef(null);
  const request = useRef(null);

  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => {
    if (fields.email) {
      email.current.focus();
    } else if (fields.password) {
      password.current.focus();
    }
  }, [fields]);

  async function submit(event) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const controller = new AbortController();
    request.current = controller;
    setPending(true);
    setFields({});
    setMessage('');

    try {
      const response = await submitAuth(signup ? 'signup' : 'login', {
        email: form.get('email'),
        password: form.get('password'),
      }, controller.signal);

      if (response.ok) {
        onSuccess();
      } else if (response.status === 401) {
        setMessage('The email or password could not be verified.');
      } else if (response.status === 409) {
        setFields({ email: ['An account already uses this email address.'] });
      } else if (response.status === 400) {
        const problem = await response.json();
        if (problem.errors) {
          setFields(problem.errors);
        } else {
          setMessage(problem.detail || 'Check the form and try again.');
        }
      } else {
        setMessage('The request failed. Try again.');
      }
    } catch {
      if (!controller.signal.aborted) {
        setMessage('Could not complete the request. Try again.');
      }
    } finally {
      if (!controller.signal.aborted) {
        setPending(false);
      }
    }
  }

  return (
    <form onSubmit={submit} aria-busy={pending}>
      <p>
        <label htmlFor="email">Email</label><br />
        <input
          ref={email}
          id="email"
          name="email"
          type="email"
          autoComplete="username"
          maxLength={256}
          required
          aria-invalid={Boolean(fields.email)}
          aria-describedby={fields.email ? 'email-error' : undefined}
        />
      </p>
      {fields.email && <p id="email-error">{fields.email.join(' ')}</p>}
      <p>
        <label htmlFor="password">Password</label><br />
        <input
          ref={password}
          id="password"
          name="password"
          type="password"
          autoComplete={signup ? 'new-password' : 'current-password'}
          maxLength={128}
          required
          aria-invalid={Boolean(fields.password)}
          aria-describedby={fields.password ? 'password-error' : undefined}
        />
      </p>
      {fields.password && <p id="password-error">{fields.password.join(' ')}</p>}
      {message && <p role="alert">{message}</p>}
      <button type="submit" disabled={pending}>
        {signup ? 'Create account' : 'Sign in'}
      </button>
    </form>
  );
}

function Account({ heading, onSignedOut }) {
  const [account, setAccount] = useState(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [signingOut, setSigningOut] = useState(false);
  const [signOutFailed, setSignOutFailed] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      try {
        const response = await getAccount(controller.signal);
        if (response.status === 401) {
          onSignedOut(false);
        } else if (response.ok) {
          setAccount(await response.json());
        } else {
          setLoadFailed(true);
        }
      } catch {
        if (!controller.signal.aborted) {
          setLoadFailed(true);
        }
      }
    }
    load();
    return () => controller.abort();
  }, [attempt, onSignedOut]);

  async function signOut() {
    setSigningOut(true);
    setSignOutFailed(false);
    try {
      const response = await submitAuth('logout');
      if (response.ok || response.status === 401) {
        onSignedOut(response.status === 401);
        return;
      }
    } catch {
      // Keep the account visible so the user can retry.
    }
    setSignOutFailed(true);
    setSigningOut(false);
  }

  return (
    <main>
      <h1 ref={heading} tabIndex={-1}>Personal Finance</h1>
      {loadFailed ? (
        <>
          <p role="alert">Could not load your account.</p>
          <button type="button" onClick={() => {
            setLoadFailed(false);
            setAttempt(attempt + 1);
          }}>Retry</button>
        </>
      ) : account ? (
        <>
          <p>Signed in as {account.email}</p>
          <p>Your account is empty.</p>
          {signOutFailed && <p role="alert">Could not sign out. Try again.</p>}
          <button type="button" onClick={signOut} disabled={signingOut}>Sign out</button>
        </>
      ) : <p role="status">Loading account…</p>}
    </main>
  );
}

createRoot(document.getElementById('root')).render(<App />);
