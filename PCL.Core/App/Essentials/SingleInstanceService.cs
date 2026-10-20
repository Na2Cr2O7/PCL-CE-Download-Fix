using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PCL.Core.App.IoC;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例", false)]
public sealed partial class SingleInstanceService
{
    private static FileStream? _lockStream;
    private static readonly string _LockFilePath = Path.Combine(Paths.SharedLocalData, "instance.lock");

    private static void _TryRpc(string processId, string content)
    {
        var pipeName = $"{RpcService.PipePrefix}@{processId}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);
        using var sw = new StreamWriter(pipe, PipeComm.PipeEncoding);
        sw.WriteLine(content);
        sw.Write(PipeComm.PipeEndingChar);
        sw.Flush();
    }

    [LifecycleStart]
    private static void _Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_LockFilePath)!);
            var stream = File.Open(_LockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            Context.Debug("未发现重复实例，正在向单例锁写入信息");
            using var sw = new StreamWriter(stream, Encoding.ASCII, 8, true);
            sw.Write(Basics.CurrentProcessId);
            sw.Flush();
            _lockStream = stream;
            return;
        }
        catch (Exception ex)
        {
            // 单例锁创建失败不等于“已有实例在运行”：目录权限、杀毒软件、文件处于删除挂起状态等
            // 都可能导致创建失败。此时若锁不可读，应继续启动，而不是让整个程序以退出码 1 结束。
            Context.Warn($"创建单例锁失败，尝试读取现有锁：{_LockFilePath}", ex);
        }

        var pid = _ReadLockFile();
        if (pid is null)
        {
            Context.Warn("单例锁不可读，跳过单实例检查并继续启动");
            return;
        }

        if (pid == Basics.CurrentProcessId.ToString())
        {
            Context.Debug("单例锁属于当前进程，继续启动");
            return;
        }

        if (!_IsProcessAlive(pid))
        {
            Context.Warn($"单例锁记录的进程 {pid} 已不存在，按无实例处理并继续启动");
            return;
        }

        Context.Info($"发现重复实例 {pid}，尝试传递参数并拉起主窗口");
        try
        {
            _TryRpc(pid, "REQ cli\n" + JsonSerializer.Serialize(StartupService.UnhandledCommands, JsonCompat.SerializerOptions));
            _TryRpc(pid, "REQ activate");
        }
        catch (Exception ex) { Context.Warn("RPC 通信失败", ex); }
        finally { Context.RequestExit(1); }
    }

    private static string? _ReadLockFile()
    {
        try
        {
            using var stream = File.Open(_LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception ex)
        {
            Context.Warn("读取单例锁出错", ex);
            return null;
        }
    }

    private static bool _IsProcessAlive(string pid)
    {
        if (!int.TryParse(pid, out var processId) || processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            // PID 可能被系统复用，需确认它确实是同一个程序
            return !process.HasExited &&
                   string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Context.Warn($"无法确认单例锁中的进程 {processId} 是否存活", ex);
            return true;
        }
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (_lockStream is null) return;
        Context.Debug("正在删除单例锁");
        _lockStream.Dispose();
        File.Delete(_LockFilePath);
    }
}
