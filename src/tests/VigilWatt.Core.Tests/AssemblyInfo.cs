using Xunit;

// Log 是全局静态状态（MinLevel / 重定向目录 / 写入句柄），测试并行时多个类会互相踩
// （例如 LogTests 与 HistoryStoreTests 都会 RedirectTo）。关掉测试集内的并行，保证日志相关断言稳定。
// 对应 doc/dev/logging.md 的 N6（原计划放阶段 2，因阶段 1 引入 LogTests 而提前）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
