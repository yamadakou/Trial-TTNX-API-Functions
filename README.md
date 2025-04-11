# Trial-TTNX-API-Functions
TTNXのDockerHubで公開されているDockerイメージをもとにAzure Container AppsにデプロイするWeb APIをAzure Functionsで提供します。
* 使用するDockerイメージ、および、Composeの内容は[TTNXのDockerHub](https://hub.docker.com/r/densocreate/timetracker)を参照。
  * https://hub.docker.com/r/densocreate/timetracker

## 前提条件
* Azure Container Appsのリソースを作成可能なAzureサブスクリプションが存在する。
* [MSドキュメントを参考](https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-develop-vs)に、Visual StudioでAzure Functionsを開発可能とする。
  * https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-develop-vs

## 事前準備
* ユーザー割り当てマネージドIDを作成する。
  * スコープ：サブスクリプション
  * リージョン：東日本
  * 権限：共同作成者 or 所有者
  * 参考：https://learn.microsoft.com/ja-jp/entra/identity/managed-identities-azure-resources/how-manage-user-assigned-managed-identities?pivots=identity-mi-methods-azp#create-a-user-assigned-managed-identity

* 環境変数を設定する。
  | Name | Value |
  |---|---|
  | SUBSCRIPTION_ID | サブスクリプションID |
  | DB_PASSWORD | DBに接続するパスワード |
  | AZURE_CLIENT_ID | ユーザー割り当てマネージドIDのクライアントID |

* Azure Functionsの環境変数からアプリ設定を行う。
  * 設定方法については [MSドキュメント参照](https://learn.microsoft.com/ja-jp/azure/azure-functions/functions-how-to-use-azure-function-app-settings)
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
          "DB_PASSWORD": "<DB接続パスワード>"
        }
      }
      ```

## 提供するWebAPI
### TTNX環境のコンテナアプリをデプロイ
POST /trial-ttnx

#### パラメータ
| Name | Value |
|---|---|
| userName | ユーザー名の文字列。この名前でリソースグループを作成する。すでに存在する名前を指定するとエラー。 |

#### レスポンス
| Name | Value |
|---|---|
| Message | 正常終了、もしくはエラー時のメッセージ。 |
| WebAppFQDN | デプロイしたWebアプリのFQDN。正常終了時のみ返す。 |
| ResourceGroupName | 作成したリソースグループの名前。正常終了時のみ返す。 |
| VNetName | 仮想ネットワークの名前。正常終了時のみ返す。 |
| SubnetName | サブネットの名前。正常終了時のみ返す。 |

#### 呼び出しイメージ
1. TTNX環境コンテナアプリのデプロイを依頼

```
POST https://・・・/TrialTTNXService_HttpStart
Body
{
  "userName": "Test-Customer-A"
}

202 Accepted
{
  "Id": "{関数キー}",
  "StatusQueryGetUri": "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}?code=・・・",
  "SendEventPostUri": "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}/raiseEvent/{eventName}?code=・・・",
  "TerminatePostUri": "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}/terminate?reason={{text}}&code=・・・",
  "RewindPostUri": null,
  "PurgeHistoryDeleteUri": "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}?code=・・・",
  "RestartPostUri": null,
  "SuspendPostUri": "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}/suspend?reason={{text}}&code=・・・",
  "ResumePostUri": "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}/resume?reason={{text}}&code=・・・"
}
```

2. TTNX環境コンテナアプリのデプロイ状況を確認
前述1.のレスポンスにある `StatusQueryGetUri` のURLを呼び出す。

##### デプロイ中の場合
* `runtimeStatus` が `Running` となる。
* `customStatus` にリソース作成などの状況を確認を表すメッセージが格納。

```
GET "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}?code=・・・

202 Accepted
{
  "name": "TrialTTNXService",
  "instanceId": "・・・",
  "runtimeStatus": "Running",
  "input": "{\n  \"userName\": \"Test-Customer-A\"\n}",
  "customStatus": "Begin.Crate.ContainerAppEnvironment",
  "output": null,
  "createdTime": "2025-04-11T17:20:55Z",
  "lastUpdatedTime": "2025-04-11T17:21:10Z"
}
```

##### デプロイ完了の場合
* `runtimeStatus` が `Completed` となる。
* `customStatus` に `End` を格納。
* `output` にデプロイしたTTNX環境コンテナアプリ（web）のFQDNを格納。
  * このFQDNにてTTNX環境コンテナアプリにアクセスする。
    * URL：https://web.・・・.japaneast.azurecontainerapps.io

```
GET "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}?code=・・・

200 OK
{
"name": "TrialTTNXService",
"instanceId": "・・・",
"runtimeStatus": "Completed",
"input": "{\n  \"userName\": \"Test-Customer-A\"\n}",
"customStatus": "End",
"output": [
"web.・・・.japaneast.azurecontainerapps.io"
],
"createdTime": "2025-04-11T17:20:55Z",
"lastUpdatedTime": "2025-04-11T17:31:03Z"
}
```

##### デプロイ失敗の場合
* `runtimeStatus` が `Completed` となる。
* `customStatus` に `Failed` を格納。
* `output` にエラー情報（例外情報）を格納。

```
GET "http://{host;port}/runtime/webhooks/durabletask/instances/{関数キー}?code=・・・

200 OK
{
  "name": "TrialTTNXService",
  "instanceId": "・・・",
  "runtimeStatus": "Completed",
  "input": "{\n  \"userName\": \"Test-Customer-A\"\n}",
  "customStatus": "Failed",
  "output": [
    "{\r\n  \"Message\": \"Task \\u0027TrialTTNXCreateResourceGroup\\u0027 (#0) failed with an unhandled exception: The resource group \\u0027Test-Customer-A\\u0027 has already been created. Please specify a different resource group name.\",\r\n  \"StackTrace\": \"   at Microsoft.DurableTask.Worker.Shims.TaskOrchestrationContextWrapper.CallActivityAsync[T](TaskName name, Object input, TaskOptions options)\\r\\n   at TrialTTNXDurableFunctions.TrialTTNXService.RunOrchestrator(TaskOrchestrationContext context) in ・・・"
  ],
  "createdTime": "2025-04-11T17:38:41Z",
  "lastUpdatedTime": "2025-04-11T17:38:43Z"
}
```