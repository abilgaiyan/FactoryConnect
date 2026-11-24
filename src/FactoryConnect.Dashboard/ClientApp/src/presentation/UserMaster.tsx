import { useEffect, useState, type FormEvent } from "react";

type User = { userId: string; username: string; role: string; isActive: boolean };

export function UserMaster() {
  const [users, setUsers] = useState<User[]>([]);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("Supervisor");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);

  async function load(signal?: AbortSignal) {
    setLoading(true);
    try {
      const response = await fetch("/api/auth/v1/users", { cache: "no-store", ...(signal ? { signal } : {}) });
      if (!response.ok) throw new Error("User list unavailable");
      const data: User[] = await response.json();
      if (!signal?.aborted) setUsers(data);
    } catch {
      if (!signal?.aborted) setError("Unable to load users. Check your session and server connection.");
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, []);

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const response = await fetch("/api/auth/v1/users", {
        method: "POST", cache: "no-store",
        headers: { "Content-Type": "application/json", "X-FC-Request": "create-user" },
        body: JSON.stringify({ username, password, role }),
      });
      if (response.status === 409) setError("Username already exists.");
      else if (response.status === 400) setError("Check username, password and role.");
      else if (!response.ok) setError("Unable to create user.");
      else {
        setUsername("");
        setNotice("User created.");
        await load();
      }
    } catch {
      setError("Unable to create user. Check the connection before retrying.");
    } finally {
      setPassword("");
      setBusy(false);
    }
  }

  return <main className="user-master">
    <h1>User Master</h1>
    {error && <p role="alert">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    <button type="button" disabled={loading || busy} onClick={() => { setError(""); void load(); }}>Refresh users</button>
    {loading ? <p role="status">Loading users…</p> :
      <table><thead><tr><th>Username</th><th>Role</th><th>Status</th></tr></thead>
        <tbody>{users.map(user => <tr key={user.userId}><td>{user.username}</td><td>{user.role}</td><td>{user.isActive ? "Active" : "Inactive"}</td></tr>)}</tbody>
      </table>}
    <form onSubmit={event => void create(event)}>
      <h2>Add user</h2>
      <label htmlFor="new-user-name">Username</label>
      <input id="new-user-name" required minLength={3} maxLength={64} pattern="[A-Za-z0-9._-]+" value={username} onChange={event => setUsername(event.currentTarget.value)} />
      <label htmlFor="new-user-password">Password (at least 14 characters)</label>
      <input id="new-user-password" type="password" autoComplete="new-password" required minLength={14} maxLength={256} value={password} onChange={event => setPassword(event.currentTarget.value)} />
      <label htmlFor="new-user-role">Role</label>
      <select id="new-user-role" value={role} onChange={event => setRole(event.currentTarget.value)}>
        <option>Supervisor</option><option>Manager</option><option>Administrator</option>
      </select>
      <button type="submit" disabled={busy}>{busy ? "Saving…" : "Create user"}</button>
    </form>
  </main>;
}
