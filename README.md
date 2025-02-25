# BackgroundTasksSample

## 🚀 概要
このプロジェクトは、ASP.NET Core の **バックグラウンドタスク (Hosted Services)** を活用して、非同期処理を管理するサンプルです。

**主な機能:**
- **`QueuedHostedService`** - 非同期キューでバックグラウンドタスクを処理
- **`TimedHostedService`** - 一定間隔でタスクを実行するサービス
- **`ConsumeScopedServiceHostedService`** - スコープ付きのバックグラウンド処理を実行
- **`MonitorLoop`** - キーボード入力 (`W`キー) でタスクをキューに追加

---

## 📂 **プロジェクト構成**

```plaintext
BackgroundTasksSample/
│── Program.cs                  # サービスの登録とアプリケーションの設定
│── Services/
│   ├── MonitorLoop.cs          # ユーザー入力を監視しタスクをキューに追加
│   ├── BackgroundTaskQueue.cs  # 非同期キュー管理
│   ├── QueuedHostedService.cs  # キューに追加されたタスクを処理
│   ├── TimedHostedService.cs   # 定期的なタスクの実行
│   ├── ConsumeScopedServiceHostedService.cs # スコープ付きタスクの処理
│   ├── ScopedProcessingService.cs  # スコープ内の処理を実装
│── README.md                   # プロジェクトの説明
```

---

## 📌 **主要コンポーネントの解説**

### 1️⃣ **`MonitorLoop.cs`** (ユーザー入力監視)
- **機能**: コンソールのキーボード入力 (`W`キー) を監視し、タスクをキューに追加。
- **使い方**: `W`キーを押すと、3回の5秒間隔での処理を実行。

```csharp
public void StartMonitorLoop()
{
    Task.Run(async () => await MonitorAsync());
}

private async ValueTask MonitorAsync()
{
    while (!_cancellationToken.IsCancellationRequested)
    {
        var keyStroke = Console.ReadKey();
        if (keyStroke.Key == ConsoleKey.W)
        {
            await _taskQueue.QueueBackgroundWorkItemAsync(BuildWorkItem);
        }
    }
}
```

---

### 2️⃣ **`BackgroundTaskQueue.cs`** (非同期キュー管理)
- **機能**: 非同期キュー (`Channel<T>`) を使い、バックグラウンドタスクを管理。
- **重要ポイント**:
  - `QueueBackgroundWorkItemAsync()` でタスクを追加。
  - `DequeueAsync()` でタスクを取得。
  - キューが満杯の場合 `BoundedChannelFullMode.Wait` で制限。

```csharp
private readonly Channel<Func<CancellationToken, ValueTask>> _queue;

public async ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem)
{
    await _queue.Writer.WriteAsync(workItem);
}

public async ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
{
    return await _queue.Reader.ReadAsync(cancellationToken);
}
```

---

### 3️⃣ **`QueuedHostedService.cs`** (キュー処理サービス)
- **機能**: `BackgroundTaskQueue` からタスクを取得し、バックグラウンドで処理。
- **重要ポイント**:
  - `ExecuteAsync()` でキューを監視し、タスクを実行。
  - 例外処理 (`try-catch`) を追加。

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var workItem = await TaskQueue.DequeueAsync(stoppingToken);
        try
        {
            await workItem(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred executing work item.");
        }
    }
}
```

---

### 4️⃣ **`TimedHostedService.cs`** (定期実行サービス)
- **機能**: 5秒ごとにバックグラウンドタスクを実行。
- **使い方**:
  - `StartAsync()` で `Timer` を設定し、5秒ごとに `DoWork()` を呼び出す。

```csharp
public Task StartAsync(CancellationToken stoppingToken)
{
    _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    return Task.CompletedTask;
}

private void DoWork(object? state)
{
    _logger.LogInformation("Timed Hosted Service is working.");
}
```

---

## 🔧 **セットアップ & 実行方法**

### 1️⃣ **プロジェクトのクローン**
```bash
git clone https://github.com/your-repo/BackgroundTasksSample.git
cd BackgroundTasksSample
```

### 2️⃣ **環境設定 (オプション)**
`appsettings.json` または環境変数で、**キューの最大容量** を設定可能。
```json
{
  "QueueCapacity": "100"
}
```

### 3️⃣ **実行**
```bash
dotnet run
```

### 4️⃣ **コンソールで `W` キーを押す**
タスクがキューに追加され、バックグラウンドで実行されます。

---

## 📌 **学べること**
✅ `IHostedService` / `BackgroundService` の使い方  
✅ `Channel<T>` を活用した非同期キュー管理  
✅ `Scoped Service` を使った依存関係管理  
✅ `CancellationToken` を用いたキャンセル処理  

