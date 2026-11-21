import type { TemplateSummary } from "../types";

function displayValue(value: string | undefined) {
  return value?.trim() || "—";
}

export function TemplateSessionMetadata({
  template,
}: {
  template: Pick<
    TemplateSummary,
    "title" | "subject" | "gradeLabel" | "category" | "course" | "maximumMistakesToPass"
  >;
}) {
  return (
    <dl className="reception-template-summary" aria-label="使用するひな形">
      <div className="reception-template-summary__title">
        <dt>試験名</dt>
        <dd>{template.title}</dd>
      </div>
      <div>
        <dt>教科</dt>
        <dd>{displayValue(template.subject)}</dd>
      </div>
      <div>
        <dt>学年</dt>
        <dd>{displayValue(template.gradeLabel)}</dd>
      </div>
      <div>
        <dt>カテゴリ</dt>
        <dd>{displayValue(template.category)}</dd>
      </div>
      <div>
        <dt>コース</dt>
        <dd>{displayValue(template.course)}</dd>
      </div>
      <div>
        <dt>合格基準</dt>
        <dd>{template.maximumMistakesToPass == null ? "未設定" : `${template.maximumMistakesToPass}ミスまで合格`}</dd>
      </div>
    </dl>
  );
}
