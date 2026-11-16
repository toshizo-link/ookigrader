# Ooki Grader 0.9.20　ホストPCの更新とスクマネの受信確認

## 1. ホストPCを更新する

実送信が有効な場合、更新後は待機中の配信も処理されます。試験用の宛先だけに送る場合は、更新前に待機中の宛先をご確認ください。過去に「送信結果不明」となった分は、そのまま残してください。

1. 採点・取り込み・配信の操作を終え、ホストPCで更新用ZIPをダウンロードします。[Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.20/OokiGrader-0.9.20-Windows-Host-Update.zip)
2. ZIPを右クリックして「すべて展開」を選び、中の `01-Update-OokiGrader-Host.cmd` を右クリックして「管理者として実行」します。
3. 更新先が **0.9.20** であることと、バックアップの状態を確認して進めます。`UPDATE WITHOUT BACKUP` と表示され、バックアップの有無が分からない場合は開発担当にご連絡ください。
4. `Ooki Grader host update completed successfully.` と表示されたら、いつものURLで名簿と過去の採点結果を開きます。

## 2. ダミー答案1件で受信を確認する

1. 受信を確認できるテスト用の生徒・保護者を選び、新しいダミー答案を1件だけ確定します。
2. 確認用設定（`dryRun: true`）では宛先の確認まで進みます。実送信への切り替えは待機分すべてに適用されるため、試験用の配信だけが待機していることを確認してから切り替えてください。[スクマネの設定手順](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.20/docs/operations/school-manager-setup-client-ja.md)
3. スクマネの「送信済みメッセージ」で、今回の宛先に「採点結果のお知らせ」が1件あることを確認します。テスト用保護者のアカウントでも、そのメッセージから添付PDFを開いてください。

送信済み一覧に見当たらない場合は、再送する前に、Ooki Graderに管理者でログインし、利用URLの末尾に `/api/v1/admin/school-manager/deliveries?limit=200` を付けて開きます。開発担当には今回の試行時刻と `lastErrorCode` をお知らせください。
