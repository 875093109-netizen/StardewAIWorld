using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;

namespace AIWorld.Core.Persistence;

/// <summary>
/// A1 — 每存档 commitment 持久化 store。包装 AIWorldSaveRepository，读写
/// AIWorldSaveData.TravelCommitments（additive 字段，旧存档反序列化为空）。
/// 运行时由 host 在 SaveLoaded/Saving/ReturnedToTitle 驱动；repo 置空时读写安全退化为空。
/// 通过 Func 延迟解析 repository，兼容其随存档生命周期变化（SaveLoaded 建、ReturnedToTitle 空）。
/// </summary>
public sealed class SaveDataCommitmentStore : ICommitmentStore
{
    private readonly Func<AIWorldSaveRepository?> _repoProvider;

    public SaveDataCommitmentStore(Func<AIWorldSaveRepository?> repoProvider)
        => _repoProvider = repoProvider;

    public void Save(IReadOnlyList<TravelCommitment> commitments)
    {
        var repo = _repoProvider();
        if (repo?.Data is not { } data) return;
        data.TravelCommitments = commitments.ToList();
        repo.MarkDataDirty();
    }

    public IReadOnlyList<TravelCommitment> Load()
    {
        var repo = _repoProvider();
        return repo?.Data?.TravelCommitments ?? new List<TravelCommitment>();
    }
}
