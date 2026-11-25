import { useEffect, useState, type FormEvent } from "react";

type User = { userId: string; username: string; role: string; isActive: boolean };

export function UserMaster({ currentUsername, onSessionEnded }: { currentUsername: string; onSessionEnded: () => void }) {
  const [resetUser, setResetUser] = useState<User | null>(null);
  const [newPassword, setNewPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [editing, setEditing] = useState<User | null>(null);
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

  async function saveAccess(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editing) return;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const response = await fetch(`/api/auth/v1/users/${encodeURIComponent(editing.userId)}/access`, {
        method: "PUT", cache: "no-store",
        headers: { "Content-Type": "application/json", "X-FC-Request": "change-access" },
        body: JSON.stringify({ role: editing.role, isActive: editing.isActive }),
      });
      if (!response.ok) setError("Unable to update access. Refresh users and check administrator permissions.");
      else {
        setEditing(null);
        setNotice("User access updated.");
        await load();
      }
    } catch {
      setError("Unable to confirm update. Refresh users before retrying.");
    } finally {
      setBusy(false);
    }
  }

  async function resetPassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!resetUser) return;
    if (newPassword !== confirmation) { setError("Passwords do not match."); return; }
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const response = await fetch(`/api/auth/v1/users/${encodeURIComponent(resetUser.userId)}/password`, {
        method: "PUT", cache: "no-store",
        headers: { "Content-Type": "application/json", "X-FC-Request": "reset-password" },
        body: JSON.stringify({ password: newPassword }),
      });
      if (response.status === 401) onSessionEnded();
      else if (!response.ok) setError("Unable to reset password.");
      else if (resetUser.username === currentUsername) onSessionEnded();
      else { setResetUser(null); setNotice("Password reset. Existing sessions have been invalidated."); }
    } catch { setError("Unable to confirm password reset. Check the connection before retrying."); }
    finally { setNewPassword(""); setConfirmation(""); setBusy(false); }
  }

  return <main className="user-master">
    <h1>User Master</h1>
    {error && <p role="alert">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    <button type="button" disabled={loading || busy} onClick={() => { setError(""); void load(); }}>Refresh users</button>
    {loading ? <p role="status">Loading users…</p> :
      <table><thead><tr><th>Username</th><th>Role</th><th>Status</th><th>Actions</th></tr></thead>
        <tbody>{users.map(user => <tr key={user.userId}><td>{user.username}</td><td>{user.role}</td><td>{user.isActive ? "Active" : "Inactive"}</td><td>{user.username === currentUsername ? "Current account" : <button type="button" disabled={busy} onClick={() => { setResetUser(null); setNewPassword(""); setConfirmation(""); setEditing(user); }}>Edit access</button>} <button type="button" disabled={busy} onClick={() => { setEditing(null); setResetUser(user); setNewPassword(""); setConfirmation(""); setError(""); setNotice(""); }}>Reset password</button></td></tr>)}</tbody>
      </table>}
    {resetUser && <form onSubmit={event => void resetPassword(event)}>
      <h2>Reset password: {resetUser.username}</h2>
      <p>Existing sessions will be invalidated. {resetUser.username === currentUsername && "You will need to sign in again."}</p>
      <label htmlFor="reset-password">New password (at least 14 characters)</label>
      <input id="reset-password" type="password" autoComplete="new-password" required minLength={14} maxLength={256} disabled={busy} value={newPassword} onChange={event => setNewPassword(event.currentTarget.value)} />
      <label htmlFor="confirm-password">Confirm password</label>
      <input id="confirm-password" type="password" autoComplete="new-password" required minLength={14} maxLength={256} disabled={busy} value={confirmation} onChange={event => setConfirmation(event.currentTarget.value)} />
      <button type="submit" disabled={busy}>Reset password</button>
      <button type="button" disabled={busy} onClick={() => { setResetUser(null); setNewPassword(""); setConfirmation(""); }}>Cancel</button>
    </form>}
    {editing && <form onSubmit={event => void saveAccess(event)}>
      <h2>Edit access: {editing.username}</h2>
      <label htmlFor="edit-user-role">Role</label>
      <select id="edit-user-role" disabled={busy} value={editing.role} onChange={event => setEditing({ ...editing, role: event.currentTarget.value })}>
        <option>Supervisor</option><option>Manager</option><option>Administrator</option>
      </select>
      <label><input type="checkbox" disabled={busy} checked={editing.isActive} onChange={event => setEditing({ ...editing, isActive: event.currentTarget.checked })} /> Active account</label>
      <button type="submit" disabled={busy || loading}>Save access</button>
      <button type="button" disabled={busy} onClick={() => setEditing(null)}>Cancel</button>
    </form>}
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
