# Ooki Grader 0.9.22　ホストPCの更新手順

1. 実送信が有効な場合は、更新後、これまで配信一覧に出ていなかった対象の確定答案も処理されます。試験中の場合は対象答案と宛先を確認し、必要に応じて確認用設定（ドライラン）に切り替えます。[スクマネの設定手順](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.22/docs/operations/school-manager-setup-client-ja.md)
2. 採点・取り込み・配信の操作を終え、ホストPCでZIPをダウンロードし、右クリックして「すべて展開」を選びます。[Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.22/OokiGrader-0.9.22-Windows-Host-Update.zip)
3. 展開したフォルダーの `01-Update-OokiGrader-Host.cmd` を右クリックして「管理者として実行」し、更新先が **0.9.22** であることとバックアップの状態を確認して進めます。
4. `Ooki Grader host update completed successfully.` と表示されたら、いつものURLで名簿と採点結果を開きます。以前の送信結果を確認する場合は、スクマネの送信済みメッセージで宛先・件名・添付PDFを照合してください。
