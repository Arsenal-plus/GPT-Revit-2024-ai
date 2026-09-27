// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Reading somebody else's .bcfzip back: markup.bcf and its viewpoint.bcfv,
// BOTH BCF 2.1 and 3.0 shapes. This is pure zip/XML arithmetic - no Autodesk.*
// anywhere in this file - so it lives in Core and is tested WITHOUT a model,
// same as CoordinationRules and IfcGuidCodec beside it. What a topic's
// Components MEAN about a finding this model measured is Commands'
// CoordinationImport.cs's job; this file only says what the bytes contain.
//
// BCF 2.1 repeats a whole &lt;Viewpoints Guid="..."&gt; element per viewpoint, each
// wrapping one &lt;Viewpoint&gt;file&lt;/Viewpoint&gt;. BCF 3.0 has ONE &lt;Viewpoints&gt;
// wrapping several &lt;ViewPoint Guid="..."&gt; children, each again with its own
// &lt;Viewpoint&gt;file&lt;/Viewpoint&gt;. Both shapes are read here; only the nesting differs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Horizun.Revit.Core
{
    /// <summary>One BCF topic (markup.bcf), plus every Component its viewpoint(s) name.</summary>
    public sealed class BcfTopic
    {
        public string Entry;
        public string Guid;
        public string Status;
        public string Title;
        public string Priority;
        public string AssignedTo;

        /// <summary>The topic's own CreationDate: the only external date a topic with no comments has.</summary>
        public string CreationDate;
        public readonly List<BcfComment> Comments = new List<BcfComment>();

        /// <summary>
        /// Every Component this topic's viewpoint(s) name - IfcGuid and/or AuthoringToolId,
        /// exactly what the file itself carries. Empty for a topic with no viewpoint, or one
        /// this reader could not parse; never invented.
        /// </summary>
        public readonly List<BcfExternalComponent> Components = new List<BcfExternalComponent>();
    }

    public sealed class BcfComment
    {
        public string Guid;
        public string Date;
        public string Author;
        public string Text;
    }

    /// <summary>One Components/Selection/Component entry from ANY tool's viewpoint.bcfv.</summary>
    public sealed class BcfExternalComponent
    {
        public string IfcGuid;
        public string AuthoringToolId;
    }

    public static class BcfMarkupReader
    {
        /// <summary>
        /// Read every topic out of a .bcfzip: its markup.bcf fields, comments, and every
        /// Component its declared viewpoint(s) name. A markup that declares a viewpoint the
        /// zip does not hold, or a viewpoint that is not valid XML, degrades that ONE topic
        /// to zero components - it does not fail the read, because one broken viewpoint is
        /// not every topic's fault. A broken markup.bcf itself does fail the whole read: a
        /// partial import of somebody else's coordination file is worse than none, because
        /// nobody can tell which half arrived.
        /// </summary>
        public static bool TryReadTopics(string path, out List<BcfTopic> topics, out string error)
        {
            topics = new List<BcfTopic>();
            error = null;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!entry.FullName.EndsWith("markup.bcf", StringComparison.OrdinalIgnoreCase)) continue;
                        var xml = new XmlDocument();
                        using (Stream entryStream = entry.Open()) xml.Load(entryStream);
                        BcfTopic topic = ReadTopic(xml, entry.FullName);
                        if (topic == null) continue;

                        string folder = entry.FullName.Substring(0, entry.FullName.Length - "markup.bcf".Length);
                        foreach (string viewpointFile in DeclaredViewpointFiles(xml.DocumentElement))
                        {
                            ZipArchiveEntry vpEntry = zip.GetEntry(folder + viewpointFile);
                            if (vpEntry == null) continue;
                            try
                            {
                                var vpXml = new XmlDocument();
                                using (Stream vpStream = vpEntry.Open()) vpXml.Load(vpStream);
                                topic.Components.AddRange(ReadComponents(vpXml.DocumentElement));
                            }
                            catch (XmlException) { /* this topic's viewpoint is unreadable; it just resolves fewer components */ }
                        }
                        topics.Add(topic);
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                error = "'" + path + "' could not be opened as a zip: " + ex.Message +
                        ". A BCF is a zip; nothing was read.";
                return false;
            }
            catch (XmlException ex)
            {
                // A file with one broken topic is not a file with none: say which entry.
                error = "a markup.bcf entry inside '" + path + "' is not valid XML: " + ex.Message +
                        ". Nothing was imported - a partial import of somebody else's coordination file is " +
                        "worse than none, because nobody can tell which half arrived.";
                return false;
            }
            catch (Exception ex)
            {
                error = "'" + path + "' could not be read: " + ex.Message;
                return false;
            }
            return true;
        }

        private static BcfTopic ReadTopic(XmlDocument xml, string entryName)
        {
            XmlElement root = xml.DocumentElement;
            if (root == null || root.Name != "Markup") return null;
            XmlNode topicNode = root.SelectSingleNode("Topic");
            if (topicNode == null) return null;

            var topic = new BcfTopic
            {
                Entry = entryName,
                Guid = Attribute(topicNode, "Guid"),
                Status = Attribute(topicNode, "TopicStatus"),
                Title = Text(topicNode, "Title"),
                Priority = Text(topicNode, "Priority"),
                AssignedTo = Text(topicNode, "AssignedTo"),
                CreationDate = Text(topicNode, "CreationDate")
            };

            foreach (XmlNode commentNode in root.SelectNodes("Comment"))
                topic.Comments.Add(new BcfComment
                {
                    Guid = Attribute(commentNode, "Guid"),
                    Date = Text(commentNode, "Date"),
                    Author = Text(commentNode, "Author"),
                    Text = Text(commentNode, "Comment")
                });

            return string.IsNullOrWhiteSpace(topic.Guid) ? null : topic;
        }

        /// <summary>
        /// The viewpoint filename(s) a topic's markup.bcf declares - BOTH the BCF 2.1 shape
        /// (one or more sibling &lt;Viewpoints Guid="..."&gt;&lt;Viewpoint&gt;file&lt;/Viewpoint&gt;&lt;/Viewpoints&gt;
        /// elements) and the BCF 3.0 shape (one &lt;Viewpoints&gt; wrapping several
        /// &lt;ViewPoint Guid="..."&gt;&lt;Viewpoint&gt;file&lt;/Viewpoint&gt;&lt;/ViewPoint&gt; children). Both carry
        /// the file name in a child element literally named "Viewpoint"; only the nesting differs.
        /// </summary>
        internal static List<string> DeclaredViewpointFiles(XmlElement markupRoot)
        {
            var files = new List<string>();
            if (markupRoot == null) return files;
            foreach (XmlNode viewpointsNode in markupRoot.SelectNodes("Viewpoints"))
            {
                foreach (XmlNode direct in viewpointsNode.SelectNodes("Viewpoint"))
                    if (!string.IsNullOrWhiteSpace(direct.InnerText)) files.Add(direct.InnerText.Trim());
                foreach (XmlNode nested in viewpointsNode.SelectNodes("ViewPoint"))
                {
                    XmlNode inner = nested.SelectSingleNode("Viewpoint");
                    if (inner != null && !string.IsNullOrWhiteSpace(inner.InnerText)) files.Add(inner.InnerText.Trim());
                }
            }
            return files;
        }

        /// <summary>
        /// Every Components/Selection/Component a viewpoint names, exactly as written: an
        /// IfcGuid attribute and/or an AuthoringToolId child. A component with neither is not
        /// collected - it names nothing this or any other tool could resolve.
        /// </summary>
        internal static List<BcfExternalComponent> ReadComponents(XmlElement viewpointRoot)
        {
            var list = new List<BcfExternalComponent>();
            if (viewpointRoot == null) return list;
            foreach (XmlNode c in viewpointRoot.SelectNodes("Components/Selection/Component"))
            {
                string ifcGuid = Attribute(c, "IfcGuid");
                string authoringToolId = Text(c, "AuthoringToolId");
                if (string.IsNullOrWhiteSpace(ifcGuid) && string.IsNullOrWhiteSpace(authoringToolId)) continue;
                list.Add(new BcfExternalComponent
                {
                    IfcGuid = string.IsNullOrWhiteSpace(ifcGuid) ? null : ifcGuid.Trim(),
                    AuthoringToolId = string.IsNullOrWhiteSpace(authoringToolId) ? null : authoringToolId.Trim()
                });
            }
            return list;
        }

        private static string Attribute(XmlNode node, string name)
        {
            XmlAttribute attribute = node?.Attributes?[name];
            return attribute?.Value;
        }

        private static string Text(XmlNode node, string child)
        {
            XmlNode found = node?.SelectSingleNode(child);
            return found?.InnerText;
        }

        /// <summary>
        /// When this topic last changed OUTSIDE this ledger: its newest comment, or its
        /// creation date when it has none.
        ///
        /// ISO-8601 UTC strings compare correctly as ordinals, which is why they are written
        /// that way everywhere in this codebase. A topic with no date at all yields null, and
        /// a null cannot conflict: an unknown date is not evidence that something moved.
        /// </summary>
        public static string LastExternalChange(BcfTopic topic)
        {
            string newest = topic.CreationDate;
            foreach (BcfComment comment in topic.Comments ?? new List<BcfComment>())
            {
                if (string.IsNullOrWhiteSpace(comment.Date)) continue;
                if (newest == null || string.CompareOrdinal(comment.Date, newest) > 0) newest = comment.Date;
            }
            return string.IsNullOrWhiteSpace(newest) ? null : newest;
        }

        /// <summary>
        /// A BCF TopicStatus mapped to one of this ledger's statuses, or null when it says
        /// nothing this ledger can act on.
        ///
        /// "Closed" becomes closed_by_decision and never resolved_by_model. The two are
        /// different claims: one says a person decided, the other says the geometry changed
        /// and a complete detection run proved it.
        /// </summary>
        public static string MapStatus(string bcfStatus)
        {
            switch ((bcfStatus ?? "").Trim().ToLowerInvariant())
            {
                case "closed":
                case "resolved":
                    return CoordinationRules.StatusClosedByDecision;
                case "open":
                case "active":
                case "reopened":
                    return CoordinationRules.StatusOpen;
                default:
                    return null;
            }
        }

        public static string ImportedCommentText(BcfComment comment)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(comment.Author)) parts.Add(comment.Author);
            if (!string.IsNullOrWhiteSpace(comment.Date)) parts.Add(comment.Date);
            string who = parts.Count == 0 ? "" : " [" + string.Join(" · ", parts) + "]";
            return "imported from BCF" + who + ": " + (comment.Text ?? "");
        }

        /// <summary>
        /// Has this comment already been folded in? Compared on the text it would produce,
        /// so re-importing the same file - which coordinators do - adds nothing the second time.
        /// </summary>
        public static bool AlreadyRecorded(CoordinationFinding finding, BcfComment comment)
        {
            string wanted = ImportedCommentText(comment);
            foreach (CoordinationEvent entry in finding.History ?? new List<CoordinationEvent>())
                if (entry.Kind == "comment" && string.Equals(entry.Text, wanted, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
