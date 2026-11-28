# Ooki Grader 0.9.26　ホストPCの更新手順

1. 採点・取り込み・配信が終わったら、ホストPCで更新用ZIPをダウンロードし、右クリックして「すべて展開」を選びます。[Windowsホスト更新用ZIP](https://github.com/toshizo-link/ookigrader/releases/download/v0.9.26/OokiGrader-0.9.26-Windows-Host-Update.zip)
2. 展開したフォルダーの `01-Update-OokiGrader-Host.cmd` を右クリックし、「管理者として実行」を選びます。更新先が **0.9.26** であることとバックアップの状態を確認して進めます。
3. `Ooki Grader host update completed successfully.` と表示されたら、いつものURLでOoki Graderを開きます。
4. 管理者で「管理」→「AI設定」を開き、塾の有料プロジェクトのAPIキーを登録します。続いて「Batch接続を確認」を実行します。[画面付きのGemini APIキー設定手順（PDF）](https://github.com/toshizo-link/ookigrader/releases/download/v0.9.26/OokiGrader-0.9.26-Gemini-Setup-JA.pdf)
5. 新しいテスト実施を作成するとき、「初回採点の方法」が「まとめ採点（Gemini Batch・割引料金）」になっていることを確認します。すぐに結果が必要な場合は「通常採点（即時）」を選べます。
