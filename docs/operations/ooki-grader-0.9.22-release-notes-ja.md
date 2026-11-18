# Ooki Grader 0.9.22　確定答案のスクマネ配信を修正

配信対象の答案のうち、古い50件がすでに配信一覧にあると、51件目以降の新しい答案が一覧に追加されないことがありました。配信記録を作っていない結果から順に処理するよう修正しました。

配信前に答案の採点内容や割り当て先の生徒が変わった場合は、送信を始める時点でも現在の確定結果と照合し、古いPDFの配信を取り消します。

添付PDFの準備中に確認用設定（ドライラン）へ切り替えた場合も、新しい設定を読み直してから処理するようにしました。

配信対象は、自動配信を有効にした後に確定した、原本PDFのある個人の採点結果です。実送信が有効な場合、更新後にこれまで処理されなかった対象の答案も配信されます。

[Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.22/OokiGrader-0.9.22-Windows-Host-Update.zip) ／ [更新手順](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.22/docs/operations/ooki-grader-0.9.22-update-ja.md)
