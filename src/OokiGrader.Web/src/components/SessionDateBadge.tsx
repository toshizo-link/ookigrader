export function SessionDateBadge({ date, className }: { date?: string | null; className: string }) {
  const [, month, day] = date?.split("-") ?? [];
  return (
    <div className={className} aria-label={date ?? "実施日未設定"}>
      {date ? <><span>{Number(month)}月</span><strong>{Number(day)}</strong></> : <span>日付なし</span>}
    </div>
  );
}
