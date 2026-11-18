# Ooki Grader 0.9.21　ホストPCの更新手順

1. 実送信が有効な場合、起動後に待機中の配信が進みます。更新前に待機中の宛先を確認し、採点・取り込み・配信の操作を終えてください。
2. ホストPCでZIPをダウンロードし、右クリックで「すべて展開」を選びます。[Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.21/OokiGrader-0.9.21-Windows-Host-Update.zip)
3. 展開したフォルダーの `01-Update-OokiGrader-Host.cmd` を右クリックし、「管理者として実行」を選びます。更新先が **0.9.21** であることとバックアップの状態を確認して進めます。
4. `Ooki Grader host update completed successfully.` が出たら、いつものURLで名簿と採点結果を開いてください。

## ダミー答案で確認する場合

自動配信は、対象生徒に紐づいていて選択されている保護者全員に届きます。実送信する場合は、全員が試験用の宛先であることを確認してから、新しいダミー答案を1件だけ確定してください。スクマネの「送信済みメッセージ」で件名「採点結果のお知らせ」を開き、テスト用保護者側でPDFを確認します。

以前の失敗記録は更新後も配信一覧に残り、自動再送されません。新しい試行は作成日時で確認できます。宛先だけを確認する場合はドライランを使えます。[スクマネの設定手順](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.21/docs/operations/school-manager-setup-client-ja.md)
