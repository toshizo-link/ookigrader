# Ooki Grader 0.9.21　スクマネの宛先選択とPDF添付を修正

スクマネのメッセージ一覧から宛先選択に切り替わる際、両方の検索欄が一時的に表示されると、0.9.20の自動配信が停止することがありました。0.9.21は宛先選択画面の検索欄と検索結果を使います。ログイン画面の読み込みがタイムアウトした場合も、認証情報を入力する前に画面を一度読み直します。

添付欄が表示された直後、スクマネ側でまだファイルを受け付けられない場合があるため、添付操作が有効になってからPDFを渡します。

実際の先生アカウントで旧版の停止を再現しました。更新用ZIPと同じプログラムで、許可された試験用宛先に確認用PDFを添付してメッセージを1通送信し、スクマネが発行したスレッドIDを確認しました。

[Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.21/OokiGrader-0.9.21-Windows-Host-Update.zip) ／ [更新手順](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.21/docs/operations/ooki-grader-0.9.21-update-ja.md)
