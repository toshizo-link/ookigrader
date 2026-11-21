import { useRef, useState } from "react";
import { Button, Card, ErrorState, Field, InlineAlert, LoadingState } from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, ApiError } from "../lib/api";

const path = "/admin/school-manager/message-templates";
const verdicts = {
  pass: "合格",
  fail: "不合格",
  unconfigured: "合否基準未設定",
} as const;
type Verdict = keyof typeof verdicts;
type MessageText = Record<"title" | Verdict, string>;
type Templates = MessageText & { revision: number; defaults: MessageText };

const verdictKeys = Object.keys(verdicts) as Verdict[];
const tokens = ["＜教科＞", "＜タイトル＞", "＜結果＞"] as const;
const titleLimit = 100;
const bodyLimit = 2000;

function preview(text: string, verdict: Verdict) {
  const sample = { "＜教科＞": "理科", "＜タイトル＞": "てこ・滑車の確認テスト", "＜結果＞": verdicts[verdict] };
  return text.replace(/＜(?:教科|タイトル|結果)＞/g, (token) => sample[token as keyof typeof sample]);
}

export function SchoolManagerMessageTemplates() {
  const query = useApiQuery<Templates>(path, (signal) => api.get(path, undefined, signal));
  if (query.status === "error") return <ErrorState error={query.error} onRetry={query.reload} />;
  if (!query.data || query.status === "loading") return <LoadingState />;
  return <TemplateEditor initial={query.data} reload={query.reload} />;
}

function TemplateEditor({ initial, reload }: { initial: Templates; reload: () => void }) {
  const [saved, setSaved] = useState(initial);
  const [draft, setDraft] = useState(initial);
  const [verdict, setVerdict] = useState<Verdict>("pass");
  const [insertInto, setInsertInto] = useState<"title" | "body">("body");
  const titleRef = useRef<HTMLInputElement>(null);
  const bodyRef = useRef<HTMLTextAreaElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error>();
  const [success, setSuccess] = useState(false);
  const changed = draft.title !== saved.title
    || verdictKeys.some((key) => draft[key] !== saved[key]);
  const emptyBody = verdictKeys.find((key) => !draft[key].trim());
  const validation = !draft.title.trim() ? "件名を入力してください。"
    : emptyBody ? `${verdicts[emptyBody]}の本文を入力してください。` : undefined;

  function edit(key: "title" | Verdict, value: string) {
    setDraft((current) => ({ ...current, [key]: value }));
    setSuccess(false);
    if (!(error instanceof ApiError && error.status === 412)) setError(undefined);
  }

  function insert(token: string) {
    const control = insertInto === "title" ? titleRef.current : bodyRef.current;
    if (!control) return;
    const start = control.selectionStart ?? control.value.length;
    const end = control.selectionEnd ?? start;
    if (control.value.length - (end - start) + token.length > control.maxLength) {
      setError(new Error(`差し込み後の文字数が上限（${control.maxLength}字）を超えます。`));
      return;
    }
    control.focus();
    control.setRangeText(token, start, end, "end");
    edit(insertInto === "title" ? "title" : verdict, control.value);
  }

  async function save() {
    if (!changed || validation || busy) return;
    setBusy(true);
    setError(undefined);
    setSuccess(false);
    try {
      const { title, pass, fail, unconfigured, revision } = draft;
      const result = await api.put<Templates>(path, { title, pass, fail, unconfigured, revision });
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
        <div>
          <h2>採点結果の配信文面</h2>
          <p>件名は共通です。本文は合格・不合格・合否基準未設定ごとに編集できます。</p>
        </div>
      </div>
      <form className="form-grid guardian-message-form" onSubmit={(event) => { event.preventDefault(); void save(); }}>
        <Field label="件名（全判定共通）" htmlFor="guardian-message-title">
          <input id="guardian-message-title" ref={titleRef} value={draft.title} maxLength={titleLimit}
            disabled={busy} required onFocus={() => setInsertInto("title")}
            onChange={(event) => edit("title", event.target.value)} />
        </Field>
        <div className="guardian-message-columns">
          <div className="form-grid">
            <Field label="編集する本文" htmlFor="guardian-message-verdict">
              <select id="guardian-message-verdict" value={verdict} disabled={busy}
                onChange={(event) => {
                  setVerdict(event.target.value as Verdict);
                  setInsertInto("body");
                }}>
                {Object.entries(verdicts).map(([key, label]) => <option key={key} value={key}>{label}</option>)}
              </select>
            </Field>
            <Field label={`${verdicts[verdict]}の本文`} htmlFor="guardian-message-body">
              <textarea key={verdict} id="guardian-message-body" ref={bodyRef} rows={12}
                value={draft[verdict]} maxLength={bodyLimit} required disabled={busy}
                onFocus={() => setInsertInto("body")} onChange={(event) => edit(verdict, event.target.value)} />
            </Field>
            <div className="guardian-message-insert" role="group" aria-label="差し込み項目">
              <span>{insertInto === "title" ? "件名" : "本文"}に挿入</span>
              {tokens.map((token) => (
                <Button key={token} type="button" variant="secondary" size="small" disabled={busy}
                  onMouseDown={(event) => event.preventDefault()} onClick={() => insert(token)}>
                  {token}
                </Button>
              ))}
            </div>
          </div>
          <section className="guardian-message-preview" aria-label="配信プレビュー">
            <h3>配信プレビュー</h3>
            <p className="guardian-message-sample">表示例　理科「てこ・滑車の確認テスト」</p>
            <p className="guardian-message-preview-title">{preview(draft.title.trim(), verdict)}</p>
            <p className="guardian-message-preview-body">{preview(draft[verdict], verdict)}</p>
          </section>
        </div>
        {validation && <p className="field__error" role="alert">{validation}</p>}
        {error && <InlineAlert tone="danger"
          action={error instanceof ApiError && error.status === 412
            ? <Button type="button" variant="secondary" onClick={reload}>再読み込み</Button>
            : undefined}>{error.message}</InlineAlert>}
        {success && <p role="status">配信文面を保存しました。</p>}
        <div className="guardian-message-actions">
          <Button type="submit" disabled={busy || !changed || Boolean(validation)}>{busy ? "保存中…" : "文面を保存"}</Button>
          <Button type="button" variant="secondary" size="small" disabled={busy || draft[verdict] === saved.defaults[verdict]}
            onClick={() => edit(verdict, saved.defaults[verdict])}>{verdicts[verdict]}の本文を標準に戻す</Button>
          <Button type="button" variant="secondary" size="small" disabled={busy || draft.title === saved.defaults.title}
            onClick={() => edit("title", saved.defaults.title)}>件名を標準に戻す</Button>
        </div>
      </form>
    </Card>
  );
}
