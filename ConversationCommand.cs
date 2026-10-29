using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Prints a BioConversation's entries and replies with their text resolved from the package's
// own English talk files (which quote each line themselves; ME1 keeps a conversation's lines in the *_LOC_INT package beside it),
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

        // The English files: "<name>.tlk" (and the male-voiced "<name>.tlk_M"), not the other languages.
        var talkFiles = pcc.Exports
            .Where(e => e.ClassName == "BioTlkFile" && (e.ObjectName.Name is "tlk" or "tlk_M"))
            .Select(e => new ME1TalkFile(pcc, e.UIndex))
            .ToList();
        string Text(int id)
        {
            foreach (ME1TalkFile tlk in talkFiles)
            {
                string text = tlk.FindDataById(id, false, false, false, false);
                if (!string.IsNullOrEmpty(text) && !text.StartsWith("No Data"))
                    return text;
            }
            return "";
        }

        Console.WriteLine($"== {conversation.InstancedFullPath} ({talkFiles.Count} talk files)");
        PropertyCollection props = conversation.GetProperties();
        PrintLines(props.GetProp<ArrayProperty<StructProperty>>("m_EntryList"), "entry", "ReplyListNew", Text);
        PrintLines(props.GetProp<ArrayProperty<StructProperty>>("m_ReplyList"), "reply", "EntryList", Text);
        return 0;
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
            var links = new List<string>();
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
