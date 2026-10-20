using EasyLabel.Core.Io;

namespace EasyLabel.Core.Tests
{
    public sealed class AgentMemoryFileTests : System.IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "tmp-memory", System.Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        }

        private string FilePath => System.IO.Path.Combine(_dir, ".easyai", "agent-memory.md");

        [Xunit.Fact]
        public void MissingFileReadsAsEmpty()
        {
            Xunit.Assert.Equal(string.Empty, AgentMemoryFile.Read(FilePath));
            Xunit.Assert.Equal(string.Empty, AgentMemoryFile.Read(null));
        }

        [Xunit.Fact]
        public void AddAppendsDatedOneLineNotes()
        {
            System.DateTime day = new System.DateTime(2026, 10, 10);
            AgentMemoryFile.Add(FilePath, "Scratches are NG.\nEven short ones.", day);
            string text = AgentMemoryFile.Add(FilePath, "Pads are labelled Pad.", day);
            string[] lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
            Xunit.Assert.Equal(new[] { "- 2026-10-10 Scratches are NG. Even short ones.", "- 2026-10-10 Pads are labelled Pad." }, lines);
            Xunit.Assert.Equal(text, AgentMemoryFile.Read(FilePath));
        }

        [Xunit.Fact]
        public void AnEmptyNoteIsRefusedAndALongNoteIsCut()
        {
            Xunit.Assert.Throws<System.ArgumentException>(() => AgentMemoryFile.Add(FilePath, "  \n ", System.DateTime.Now));
            string text = AgentMemoryFile.Add(FilePath, new string('x', 1000), System.DateTime.Now);
            Xunit.Assert.True(text.Length < AgentMemoryFile.MaxNoteChars + 40);
            Xunit.Assert.Contains("...", text);
        }

        [Xunit.Fact]
        public void TheOldestNotesAreDroppedWhenTheFileGrowsPastTheLimit()
        {
            System.DateTime day = new System.DateTime(2026, 10, 10);
            string last = string.Empty;
            for (int i = 0; i < 40; i++)
            {
                last = AgentMemoryFile.Add(FilePath, "note " + i + " " + new string('y', 300), day);
            }

            Xunit.Assert.True(last.Length <= AgentMemoryFile.MaxChars);
            Xunit.Assert.Contains("note 39 ", last);
            Xunit.Assert.DoesNotContain("note 0 ", last);
        }

        [Xunit.Fact]
        public void WriteAndClear()
        {
            AgentMemoryFile.Write(FilePath, "- hand edited\n");
            Xunit.Assert.Equal("- hand edited\n", AgentMemoryFile.Read(FilePath));
            Xunit.Assert.False(System.IO.File.Exists(FilePath + ".tmp"));
            AgentMemoryFile.Clear(FilePath);
            Xunit.Assert.False(System.IO.File.Exists(FilePath));
            AgentMemoryFile.Clear(FilePath);
        }

        [Xunit.Fact]
        public void TheLayoutPutsTheMemoryNextToTheProjectFile()
        {
            Xunit.Assert.Equal(System.IO.Path.Combine(ProjectLayout.MetaDir(_dir), "agent-memory.md"), ProjectLayout.AgentMemoryFile(_dir));
        }
    }
}
