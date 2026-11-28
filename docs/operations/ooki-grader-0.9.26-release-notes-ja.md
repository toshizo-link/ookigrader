# Ooki Grader 0.9.26　まとめ採点と塾のGemini APIキー

- 新しいテスト実施では、Gemini Batchの接続確認が済んでいれば「まとめ採点」が最初から選ばれます。まとめ採点には割引料金が適用され、完了の目安は送信から24時間以内です。急ぎのテストでは「通常採点（即時）」を選べます。
- 既定モデルのGemini 3.8 Flashは、テストひな形を通常リクエストで作成します。思考レベルはひな形作成がMEDIUM、答案の採点がLOWです。先生が読み取り内容を確認してからひな形を使用します。
- 答案は原則1回の読み取りで採点します。長い答案は必要に応じて分割します。判読が難しい解答などは採点後に先生が確認できます。
- 設定手順のPDFでは、Google AI Studioで最新のモデルIDと料金を調べ、同じAPIキーでモデルを変更し、採点精度を比べる方法も案内しています。

[Windowsホスト更新用ZIP](https://github.com/toshizo-link/ookigrader/releases/download/v0.9.26/OokiGrader-0.9.26-Windows-Host-Update.zip) ／ [ホストPCの更新手順](https://github.com/toshizo-link/ookigrader/releases/download/v0.9.26/OokiGrader-0.9.26-Update-Guide-JA.md) ／ [塾のGemini APIキー設定手順（PDF）](https://github.com/toshizo-link/ookigrader/releases/download/v0.9.26/OokiGrader-0.9.26-Gemini-Setup-JA.pdf)
