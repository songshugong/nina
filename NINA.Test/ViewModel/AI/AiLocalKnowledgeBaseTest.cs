using FluentAssertions;
using NINA.ViewModel.AI;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NINA.Test.ViewModel.AI {

    [TestFixture]
    public class AiLocalKnowledgeBaseTest {

        [Test]
        public async Task BuildContextAsync_ShouldIncludeMemoryAndRelevantDocs() {
            var root = CreateTempDir();
            try {
                var kb = new AiLocalKnowledgeBase(root);
                var docsDir = Path.Combine(root, "docs");
                Directory.CreateDirectory(docsDir);

                File.WriteAllText(Path.Combine(root, "memory.md"), "# memory\nalways check weather first", Encoding.UTF8);
                File.WriteAllText(Path.Combine(docsDir, "weather-policy.md"), "If cloud cover exceeds 80 then pause sequence.", Encoding.UTF8);
                File.WriteAllText(Path.Combine(docsDir, "mount-notes.md"), "Mount parking steps and recovery notes.", Encoding.UTF8);

                var context = await kb.BuildContextAsync("weather cloud pause");

                context.Should().Contain("Local memory");
                context.Should().Contain("always check weather first");
                context.Should().Contain("weather-policy.md");
            } finally {
                DeleteDir(root);
            }
        }

        [Test]
        public async Task AppendRecordAsync_ShouldPersistAndBeReadableInContext() {
            var root = CreateTempDir();
            try {
                var kb = new AiLocalKnowledgeBase(root);
                await kb.AppendRecordAsync(new AiKnowledgeRecord {
                    Type = "command_ok",
                    Prompt = "status",
                    Summary = "status => all systems nominal",
                    Source = "rules"
                });

                var context = await kb.BuildContextAsync("systems nominal");

                context.Should().Contain("Recent events");
                context.Should().Contain("all systems nominal");
            } finally {
                DeleteDir(root);
            }
        }

        private static string CreateTempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "nina-ai-kb-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDir(string path) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, true);
                }
            } catch {
            }
        }
    }
}
