import { useEffect, useState, type FormEvent, type PropsWithChildren } from "react";

import { UserMaster } from "./UserMaster.tsx";

type Session = { username: string; role: "Administrator" | "Supervisor" | "Manager" };

function parseSession(value: unknown): Session {
  if (typeof value !== "object" || value === null || !("username" in value) ||
      typeof value.username !== "string" || !("role" in value) ||
      (value.role !== "Administrator" && value.role !== "Supervisor" && value.role !== "Manager")) {
    throw new Error("Invalid session response");
  }
  return { username: value.username, role: value.role };
}

export function SessionGate({ children }: PropsWithChildren) {
  const [showUsers, setShowUsers] = useState(false);
  const [session, setSession] = useState<Session | null>(null);
  const [checking, setChecking] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    async function check() {
      try {
        const response = await fetch("/api/auth/v1/me", { cache: "no-store", signal: controller.signal });
        if (response.ok) setSession(parseSession(await response.json()));
        else if (response.status === 401) setSession(null);
        else throw new Error("Session unavailable");
      } catch {
        if (!controller.signal.aborted) setError("Unable to check session. Please try signing in.");
      } finally {
        if (!controller.signal.aborted) setChecking(false);
      }
    }
    void check();
    return () => controller.abort();
  }, []);

  async function login(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const response = await fetch("/api/auth/v1/login", {
        method: "POST", headers: { "Content-Type": "application/json", "X-FC-Request": "login" },
        body: JSON.stringify({ username, password }), cache: "no-store",
      });
      if (response.status === 401) setError("Invalid credentials or account unavailable.");
      else if (response.status === 429) setError("Too many attempts. Try again in a minute.");
      else if (!response.ok) setError("Sign-in service unavailable.");
      else setSession(parseSession(await response.json()));
    } catch {
      setError("Unable to sign in. Check the server connection.");
    } finally {
      setPassword("");
      setBusy(false);
    }
  }

  async function logout() {
    setBusy(true);
    setError("");
    try {
      const response = await fetch("/api/auth/v1/logout", {
        method: "POST", headers: { "X-FC-Request": "logout" }, cache: "no-store",
      });
      if (response.ok || response.status === 401) { setSession(null); setShowUsers(false); }
      else setError("Sign-out failed. Please retry.");
    } catch {
      setError("Sign-out failed. Please retry.");
    } finally {
      setBusy(false);
    }
  }

  if (checking) return <p role="status">Checking session…</p>;
  if (session) return <>
    <div className="session-bar"><span>{session.username} · {session.role}</span>
      {session.role === "Administrator" && <button type="button" onClick={() => setShowUsers(value => !value)}>{showUsers ? "Dashboard" : "User Master"}</button>}
      <button type="button" disabled={busy} onClick={() => void logout()}>Sign out</button>
      {error && <p role="alert">{error}</p>}
    </div>
    {showUsers && session.role === "Administrator" ? <UserMaster currentUsername={session.username} /> : children}
  </>;
  return <main className="session-login">
    <form onSubmit={event => void login(event)}>
      <h1>FactoryConnect</h1><p>Sign in to your factory workspace</p>
      <label htmlFor="fc-username">Username</label>
      <input id="fc-username" autoComplete="username" required maxLength={64} value={username}
        onChange={event => setUsername(event.currentTarget.value)} />
      <label htmlFor="fc-password">Password</label>
      <input id="fc-password" type="password" autoComplete="current-password" required maxLength={256}
        value={password} onChange={event => setPassword(event.currentTarget.value)} />
      {error && <p role="alert">{error}</p>}
      <button type="submit" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
    </form>
  </main>;
}
