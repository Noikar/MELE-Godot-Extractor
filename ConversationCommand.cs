using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Prints a BioConversation's entries and replies with their text resolved from the package's
// own English talk files (which quote each line themselves; ME1 keeps a conversation's lines in the *_LOC_INT package beside it;
// LE2 and LE3 keep theirs in the game's BIOGame_INT.tlk files beside the packages),
// plus each line's conditional and plot transition and where it leads. How ME1's Rapid Transit
// menu (sta00_cab_dlg) was read: each reply is a destination.
public static class ConversationCommand
{
    public static int Run(string package, string filter)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        ExportEntry conversation = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioConversation"
            && e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (conversation == null)
        {
            Console.Error.WriteLine($"No BioConversation matching {filter}.");
            return 1;
        }

        Func<int, string> text = TalkText(pcc, out int talkFiles);
        Console.WriteLine($"== {conversation.InstancedFullPath} ({(talkFiles > 0 ? talkFiles : "the game's")} talk files)");
        PropertyCollection props = conversation.GetProperties();
        PrintLines(props.GetProp<ArrayProperty<StructProperty>>("m_EntryList"), "entry", "ReplyListNew", text);
        PrintLines(props.GetProp<ArrayProperty<StructProperty>>("m_ReplyList"), "reply", "EntryList", text);
        return 0;
    }

    private static readonly object TlkLock = new object();

    // A package's English line text by string id ("" when it has none). ME1: the package's own
    // talk files, "<name>.tlk" and the male-voiced "<name>.tlk_M" (talkFiles counts them). LE2 and
    // LE3: every English .tlk in the package's folder (the base game's and the DLCs'), loaded once
    // per run into LEX's shared lists (talkFiles 0).
    public static Func<int, string> TalkText(IMEPackage pcc, out int talkFiles)
    {
        List<ME1TalkFile> files = pcc.Exports
            .Where(e => e.ClassName == "BioTlkFile" && (e.ObjectName.Name is "tlk" or "tlk_M"))
            .Select(e => new ME1TalkFile(pcc, e.UIndex))
            .ToList();
        talkFiles = files.Count;
        if (files.Count == 0 && pcc.Game is MEGame.LE2 or MEGame.LE3)
        {
            lock (TlkLock)
            {
                bool loaded = pcc.Game == MEGame.LE2 ? LE2TalkFiles.LoadedTlks.Count > 0 : LE3TalkFiles.LoadedTlks.Count > 0;
                foreach (string tlk in loaded ? [] : Directory.GetFiles(Path.GetDirectoryName(pcc.FilePath)!, "*_INT.tlk"))
                {
                    if (pcc.Game == MEGame.LE2)
                        LE2TalkFiles.LoadTlkData(tlk);
                    else
                        LE3TalkFiles.LoadTlkData(tlk);
                }
            }
        }
        MEGame game = pcc.Game;
        return id =>
        {
            foreach (ME1TalkFile tlk in files)
            {
                string found = tlk.FindDataById(id, false, false, false, false);
                if (!string.IsNullOrEmpty(found) && !found.StartsWith("No Data"))
                    return found;
            }
            if (files.Count == 0 && game is MEGame.LE2 or MEGame.LE3)
            {
                string found = game == MEGame.LE2 ? LE2TalkFiles.FindDataById(id, false) : LE3TalkFiles.FindDataById(id, false);
                if (!string.IsNullOrEmpty(found) && !found.StartsWith("No Data"))
                    return found;
            }
            return "";
        };
    }

    private static void PrintLines(ArrayProperty<StructProperty> lines, string kind, string linksName, Func<int, string> text)
    {
        if (lines == null)
            return;
        for (int i = 0; i < lines.Count; i++)
        {
            PropertyCollection line = lines[i].Properties;
            int id = line.GetProp<StringRefProperty>("srText")?.Value ?? -1;
            int conditional = line.GetProp<IntProperty>("nConditionalFunc")?.Value ?? -1;
            int transition = line.GetProp<IntProperty>("nStateTransition")?.Value ?? -1;

            // Entries link to replies through structs (nIndex), replies to entries through plain ints.
            List<string> links = new List<string>();
            if (line.GetProp<ArrayProperty<StructProperty>>(linksName) is { } structLinks)
            {
                foreach (StructProperty link in structLinks)
                {
                    int index = link.Properties.GetProp<IntProperty>("nIndex")?.Value ?? -1;
                    string paraphrase = link.Properties.GetProp<StrProperty>("sParaphrase")?.Value ?? "";
                    int paraphraseId = link.Properties.GetProp<StringRefProperty>("srParaphrase")?.Value ?? -1;
                    string shown = paraphraseId > 0 ? text(paraphraseId) : paraphrase;
                    links.Add(string.IsNullOrEmpty(shown) ? $"{index}" : $"{index} {shown}");
                }
            }
            else if (line.GetProp<ArrayProperty<IntProperty>>(linksName) is { } intLinks)
                links.AddRange(intLinks.Select(l => l.Value.ToString()));

            Console.WriteLine($"{kind} {i}: [{id}] {text(id)}  cond {conditional}  transition {transition}  -> {string.Join(", ", links)}");
        }
    }
}
