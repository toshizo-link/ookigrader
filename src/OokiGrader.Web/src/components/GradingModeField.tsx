import { useId, useState } from "react";
import { useRuntimeCapabilities } from "../hooks/useRuntimeCapabilities";
import type { SessionGradingMode } from "../types";
import { Field } from "./ui";

export function useBatchAvailability(enabled = true) {
  const capabilities = useRuntimeCapabilities(enabled);
  if (capabilities.status === "loading") return "loading";
  if (capabilities.status === "error") return "error";
  const batch = capabilities.data?.ai?.geminiBatch;
  if (batch?.enabled === false) return "disabled";
  return batch?.ready === true ? "ready" : "unavailable";
}

export const gradingModeLabels = {
  standard: "通常採点（即時）",
  batch: "まとめ採点（Gemini Batch・割引料金）",
};

export function useNewSessionGradingMode(open: boolean) {
  const batchAvailability = useBatchAvailability(open);
  const [selection, setGradingMode] = useState<SessionGradingMode>();
  const preferredMode: SessionGradingMode =
    batchAvailability === "ready" || batchAvailability === "loading" ? "batch" : "standard";
  return { gradingMode: selection ?? preferredMode, setGradingMode, batchAvailability } as const;
}

const batchHints = {
  ready: "",
  loading: "まとめ採点の利用可否を確認しています。",
  error: "まとめ採点の利用可否を取得できませんでした。通常採点を選ぶか、画面を再読み込みしてください。",
  unavailable: "現在は通常採点を利用できます。まとめ採点を利用するには、管理者がAI設定で「Batch接続を確認」を実行してください。",
  disabled: "このホストではまとめ採点が無効です。利用する場合は管理者に確認してください。",
};

export function GradingModeField({
  value, onChange, availability, disabled,
}: {
  value: SessionGradingMode;
  onChange: (value: SessionGradingMode) => void;
  availability: ReturnType<typeof useBatchAvailability>;
  disabled: boolean;
}) {
  const id = useId();
  const descriptionId = `${id}-description`;
  return (
    <Field htmlFor={id} label="初回採点の方法">
      <select
        id={id}
        value={value}
        disabled={disabled}
        aria-describedby={descriptionId}
        onChange={(event) => onChange(event.target.value === "batch" ? "batch" : "standard")}
      >
        <option value="standard">{gradingModeLabels.standard}</option>
        <option value="batch" disabled={availability !== "ready"}>
          {gradingModeLabels.batch}
        </option>
      </select>
      <small className="field__hint" id={descriptionId}>
        {availability === "ready" || value === "batch"
          ? "まとめ採点は送信から24時間以内が完了の目安です。要確認の設問は、採点後に先生が確認できます。"
          : null}
        {batchHints[availability]}
      </small>
    </Field>
  );
}
