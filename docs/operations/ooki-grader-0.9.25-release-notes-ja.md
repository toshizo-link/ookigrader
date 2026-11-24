# Ooki Grader 0.9.25　ひな形生成と採点結果PDF

- ひな形生成の画面で、AIの利用上限、サービス側の一時エラー、通信の中断を見分けやすくしました。失敗した項目は同じ画面から再試行できます。
- ひな形の生成中、通常モデルが混雑エラーを返した時点で予備モデルを試します。予備モデルで処理できた場合は、その後5分間は予備モデルを優先します。接続の再確認で一時エラーが出た場合も、それまで使えていたGeminiの設定を保持します。
- 解答欄の多い用紙で最初の読み取りが済み、解答欄の数の再確認だけが一時エラーで止まった場合は、同じ用紙・設定で再試行すると保存済みの読み取り結果を使って確認を続けられます。
- 暗記テストの採点結果PDFは、各問の右端に「○ 正解」「× 不正解」を表示します。空欄・判読不可も区別できます。配点が異なるテストや部分点がある場合は、各問の得点も表示します。

[Windowsホスト更新用ZIP](https://github.com/toshizo-link/ookigrader/releases/download/v0.9.25/OokiGrader-0.9.25-Windows-Host-Update.zip) ／ [更新手順](https://github.com/toshizo-link/ookigrader/blob/v0.9.25/docs/operations/ooki-grader-0.9.25-update-ja.md)
