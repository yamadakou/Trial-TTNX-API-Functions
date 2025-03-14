# Trial-TTNX-API-Functions
TTNXのDockerHubで公開されているDockerイメージをもとにAzure Container AppsにデプロイするWeb APIをAzure Functionsで提供します。
* 使用するDockerイメージ、および、Composeの内容は[TTNXのDockerHub](https://hub.docker.com/r/densocreate/timetracker)を参照。
 * https://hub.docker.com/r/densocreate/timetracker

## 前提条件
* Azure Container Appsのリソースを作成可能なAzureサブスクリプションが存在する。
* [MSドキュメントを参考](https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-develop-vs)に、Visual StudioでAzure Functionsを開発可能とする。
  * https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-develop-vs

## 事前準備
* 環境変数を設定する。
  | Name | Value |
  |---|---|
  | SUBSCRIPTION_ID | サブスクリプションID |
  | DB_PASSWORD | DBに接続するパスワード |

* Azure Functionsの環境変数からアプリ設定を行う。
  * 設定方法については[MSドキュメント参照] (https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-how-to-use-azure-function-app-settings)
    * https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-how-to-use-azure-function-app-settings 
* ローカルでデバッグ実行する際に使用する `local.settings.json` の `Values` 内に設定する。
  * local.settings.jsonファイルについては[MSドキュメント参照](https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-develop-local#local-settings-file)
    * https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-develop-local#local-settings-file
  *  local.settings.jsonファイルの設定イメージ
      ```json
      {
        ・・・
        "Values": {
          "AzureWebJobsStorage": "UseDevelopmentStorage=true",
          "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
          "SUBSCRIPTION_ID": "<サブスクリプションID>",
          "DB_PASSWORD": "<DBに接続する際のパスワード>"
        }
      }
      ```

## 提供するWebAPI
### TTNX環境のコンテナアプリをデプロイ
POST /trial-ttnx
#### パラメータ
| Name | Value |
|---|---|
| userName | ユーザー名の文字列。この名前でリソースグループを作成する。リソースグループにすでに存在する名前を指定するとエラー。 |
#### 呼び出しイメージ
POST https://・・・/trial-ttnx
```
{
  "userName": "Test-Customer-A"
}
```
