import { useState } from "react";
import { api } from "../lib/api";
import { Button, Card, Field, InlineAlert } from "./ui";
import type { TemplateSummary } from "../types";

export type PassCriteria = Pick<TemplateSummary, "id" | "revision" | "maximumMistakesToPass">;

type EditorProps = ({ template: PassCriteria; session?: never } | { session: PassCriteria; template?: never }) & {
  readOnly: boolean;
  onSaved: (criteria: PassCriteria) => void;
};

export function PassCriteriaEditor({ template, session, readOnly, onSaved }: EditorProps) {
  const source = template ?? session;
  const [value, setValue] = useState(source.maximumMistakesToPass?.toString() ?? "");
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const [saved, setSaved] = useState(false);
  const maximum = value === "" ? null : Number(value);
  const valid = maximum === null || (Number.isInteger(maximum) && maximum >= 0 && maximum <= 2000);
  async function save() {
    if (!valid || working) return;
    setWorking(true);
    setError(undefined);
    setSaved(false);
    try {
      const collection = template ? "templates" : "test-sessions";
      const result = await api.put<PassCriteria>(`/${collection}/${encodeURIComponent(source.id)}/pass-criteria`, {
        maximumMistakesToPass: maximum, revision: source.revision,
      });
      setSaved(true);
      onSaved(result);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "合格基準を保存できませんでした。");
    } finally { setWorking(false); }
  }
  const fields = <div className="pass-criteria-form">
    <Field htmlFor="maximum-mistakes" label="合格にする最大ミス数" hint="空欄は合否基準未設定です。部分正解・無解答・判読不能も、採点する1問につき1ミスと数えます。">
      <input id="maximum-mistakes" type="number" min={0} max={2000} step={1} value={value}
        disabled={readOnly || working} onChange={event => { setValue(event.target.value); setSaved(false); }} />
    </Field>
    <p aria-live="polite"><strong>{!valid ? "0〜2000の整数を入力してください。"
      : maximum === null ? "合否基準未設定（合格・不合格を判定しません）"
      : `${maximum}ミスまで合格`}</strong></p>
    {!template ? <p>基準を変更した場合、作成済みのPDFは新しく作成し直してください。</p> : null}
    {error ? <InlineAlert tone="danger">{error}</InlineAlert> : null}
    {saved ? <p role="status">合格基準を保存しました。</p> : null}
    {!readOnly ? <Button disabled={!valid || working} onClick={() => void save()}>
      {working ? "保存しています…" : "合格基準を保存"}
    </Button> : null}
  </div>;
  if (template) return fields;
  return <Card>
    <div className="card__header"><div>
      <h2>合格・不合格の判定</h2>
      <p>このテスト実施の結果画面と、新しく作成する結果PDFに適用します。</p>
    </div></div>
    {fields}
  </Card>;
}
