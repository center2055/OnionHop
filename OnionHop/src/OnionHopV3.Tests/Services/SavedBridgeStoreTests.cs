using System.IO;
using System.Linq;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;
using Xunit;

namespace OnionHopV3.Tests.Services;

public class SavedBridgeStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onionhop-savedtest-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AddRange_dedupes_by_normalized_line_and_persists()
    {
        var dir = TempDir();
        try
        {
            var store = new SavedBridgeStore(dir);
            var entry = new SavedBridge { Line = "obfs4 1.2.3.4:443 ABC cert=x", Kind = SavedBridgeKind.Bridge };

            var addedFirst = store.AddRange([entry]);
            // Same line (case/whitespace-insensitive) must not add a second copy.
            var addedSecond = store.AddRange([new SavedBridge { Line = "  OBFS4 1.2.3.4:443 ABC CERT=X  ", Kind = SavedBridgeKind.Bridge }]);

            Assert.Equal(1, addedFirst);
            Assert.Equal(0, addedSecond);

            // A fresh store instance reads what was persisted.
            var reloaded = new SavedBridgeStore(dir).Load();
            Assert.Single(reloaded);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SetLabel_and_Remove_work()
    {
        var dir = TempDir();
        try
        {
            var store = new SavedBridgeStore(dir);
            var entry = new SavedBridge { Line = "cdn.example.com", Kind = SavedBridgeKind.Sni };
            store.AddRange([entry]);
            var id = store.Load().Single().Id;

            store.SetLabel(id, "my front");
            Assert.Equal("my front", store.Load().Single().Label);

            store.Remove(id);
            Assert.Empty(store.Load());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_on_corrupt_file_returns_empty_and_quarantines()
    {
        var dir = TempDir();
        try
        {
            var store = new SavedBridgeStore(dir);
            File.WriteAllText(store.StorePath, "{ this is not valid json ][");

            Assert.Empty(store.Load());
            // The corrupt original was moved aside, not left in place to crash again.
            Assert.False(File.Exists(store.StorePath));
            Assert.NotEmpty(Directory.GetFiles(dir, "*.corrupt-*"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
