import { useState } from "react";
import { Button, Card, ErrorState, Field, InlineAlert, LoadingState } from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, ApiError } from "../lib/api";

const path = "/admin/school-manager/message-templates";
const fields = [
  ["pass", "合格のとき"],
  ["fail", "不合格のとき"],
  ["unconfigured", "合否基準がないとき"],
] as const;
type Templates = Record<(typeof fields)[number][0], string> & { revision: number };

export function SchoolManagerMessageTemplates() {
  const query = useApiQuery<Templates>(path, (signal) => api.get(path, undefined, signal));
  if (query.status === "error") return <ErrorState error={query.error} onRetry={query.reload} />;
  if (!query.data || query.status === "loading") return <LoadingState />;
  return <TemplateEditor initial={query.data} reload={query.reload} />;
}

function TemplateEditor({ initial, reload }: { initial: Templates; reload: () => void }) {
  const [saved, setSaved] = useState(initial);
  const [draft, setDraft] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error>();
  const [success, setSuccess] = useState(false);
  const changed = fields.some(([key]) => draft[key] !== saved[key]);

  async function save() {
    setBusy(true);
    setError(undefined);
    setSuccess(false);
    try {
      const result = await api.put<Templates>(path, draft);
      setDraft(result);
      setSaved(result);
      setSuccess(true);
    } catch (e) {
      setError(e instanceof Error ? e : new Error("文面を保存できませんでした。"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <div className="card__header">
        <div><h2>採点結果の配信文面</h2>
          <p>教科・テスト名・合否は自動で入ります。その下に添える文面を設定してください。</p>
        </div>
      </div>
      <form className="form-grid guardian-message-form" onSubmit={(e) => { e.preventDefault(); void save(); }}>
        {fields.map(([key, label]) => (
          <Field key={key} label={label} htmlFor={`guardian-message-${key}`}>
            <textarea id={`guardian-message-${key}`} rows={2} maxLength={1000} disabled={busy}
              value={draft[key]} onChange={(e) => {
                setDraft({ ...draft, [key]: e.target.value });
                setSuccess(false);
              }} />
          </Field>
        ))}
        {error && <InlineAlert tone="danger"
          action={error instanceof ApiError && error.status === 412
            ? <Button type="button" variant="secondary" onClick={reload}>再読み込み</Button>
            : undefined}>{error.message}</InlineAlert>}
        {success && <p role="status">配信文面を保存しました。</p>}
        <div><Button type="submit" disabled={busy || !changed}>{busy ? "保存中…" : "文面を保存"}</Button></div>
      </form>
    </Card>
  );
}
