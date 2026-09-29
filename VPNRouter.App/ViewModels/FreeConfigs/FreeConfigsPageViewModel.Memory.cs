using System.Runtime;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

public partial class FreeConfigsPageViewModel
{
    private void TrimAndReclaim()
    {
        try
        {
            var beforeCount = _allConfigs.Count;
            _allConfigs = _allConfigs
                .Where(FreeConfigKeepPolicy.ShouldKeepInLiveCache)
                .ToList();
            var afterCount = _allConfigs.Count;
            var freed = beforeCount - afterCount;

            if (freed <= 0)
            {
                _logger.Debug("[FreeConfigs] TrimAndReclaim: nothing to trim ({n} entries kept)", afterCount);
                return;
            }

            _logger.Information("[FreeConfigs] TrimAndReclaim: {before} → {after} entries ({freed} dropped)",
                beforeCount, afterCount, freed);

            try
            {
                var file = _aggregator.Cache.Load();
                file.Configs = _allConfigs;
                file.LastAggregatedAt = DateTime.UtcNow;
                _aggregator.Cache.Save(file);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[FreeConfigs] TrimAndReclaim: cache save failed (non-fatal)");
            }

            ReclaimPostSearchMemory();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] TrimAndReclaim threw — skipping");
        }
    }

    private static void ReclaimPostSearchMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        }
        catch { }

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { }

        try
        {
            SkiaSharp.SKGraphics.PurgeAllCaches();
        }
        catch { }
    }
}
