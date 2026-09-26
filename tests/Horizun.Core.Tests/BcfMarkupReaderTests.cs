// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Reading a .bcfzip from ANY tool: markup.bcf plus its viewpoint.bcfv, both the
// BCF 2.1 shape (repeated <Viewpoints> elements) and the BCF 3.0 shape (one
// <Viewpoints> wrapping several <ViewPoint> children). Every fixture here is a
// small in-memory zip built from inline XML - no model, no disk fixture file.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BcfMarkupReaderTests
    {
        private static string WriteZip(IDictionary<string, string> entries)
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-bcf-test-" + System.Guid.NewGuid().ToString("N") + ".bcfzip");
            using (FileStream stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (KeyValuePair<string, string> entry in entries)
                {
                    ZipArchiveEntry e = zip.CreateEntry(entry.Key);
                    using (Stream s = e.Open())
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(entry.Value);
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            return path;
        }

        private const string Bcf21Markup =
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Markup>
  <Topic Guid=""11111111-1111-1111-1111-111111111111"" TopicType=""Clash"" TopicStatus=""Open"">
    <Title>Duct vs Beam</Title>
    <Priority>High</Priority>
    <AssignedTo>mep@example.com</AssignedTo>
    <CreationDate>2026-01-01T00:00:00Z</CreationDate>
  </Topic>
  <Viewpoints Guid=""22222222-2222-2222-2222-222222222222"">
    <Viewpoint>viewpoint.bcfv</Viewpoint>
  </Viewpoints>
  <Comment Guid=""33333333-3333-3333-3333-333333333333"">
    <Date>2026-01-02T00:00:00Z</Date>
    <Author>coordinator@example.com</Author>
    <Comment>please move the duct</Comment>
  </Comment>
</Markup>";

        private const string Bcf21Viewpoint =
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<VisualizationInfo Guid=""44444444-4444-4444-4444-444444444444"">
  <Components>
    <Selection>
      <Component IfcGuid=""01psB8wRo$Y00000000005"">
        <OriginatingSystem>Navisworks</OriginatingSystem>
        <AuthoringToolId>111</AuthoringToolId>
      </Component>
      <Component IfcGuid=""1FGh4gRhLBFvhCiTUcE_$w"">
        <OriginatingSystem>Navisworks</OriginatingSystem>
      </Component>
    </Selection>
  </Components>
  <PerspectiveCamera>
    <CameraViewPoint><X>0</X><Y>0</Y><Z>0</Z></CameraViewPoint>
    <CameraDirection><X>1</X><Y>0</Y><Z>0</Z></CameraDirection>
    <CameraUpVector><X>0</X><Y>0</Y><Z>1</Z></CameraUpVector>
    <FieldOfView>60</FieldOfView>
  </PerspectiveCamera>
</VisualizationInfo>";

        [Fact]
        public void Reads_a_bcf_21_topic_with_its_viewpoint_components_and_comment()
        {
            string path = WriteZip(new Dictionary<string, string>
            {
                ["bcf.version"] = "<Version VersionId=\"2.1\"/>",
                ["TOPIC-1/markup.bcf"] = Bcf21Markup,
                ["TOPIC-1/viewpoint.bcfv"] = Bcf21Viewpoint
            });
            try
            {
                List<BcfTopic> topics;
                string error;
                bool ok = BcfMarkupReader.TryReadTopics(path, out topics, out error);

                Assert.True(ok, error);
                Assert.Single(topics);
                BcfTopic topic = topics[0];
                Assert.Equal("11111111-1111-1111-1111-111111111111", topic.Guid);
                Assert.Equal("Open", topic.Status);
                Assert.Equal("Duct vs Beam", topic.Title);
                Assert.Equal("High", topic.Priority);
                Assert.Equal("mep@example.com", topic.AssignedTo);
                Assert.Equal("2026-01-01T00:00:00Z", topic.CreationDate);

                Assert.Single(topic.Comments);
                Assert.Equal("please move the duct", topic.Comments[0].Text);
                Assert.Equal("coordinator@example.com", topic.Comments[0].Author);

                Assert.Equal(2, topic.Components.Count);
                Assert.Equal("01psB8wRo$Y00000000005", topic.Components[0].IfcGuid);
                Assert.Equal("111", topic.Components[0].AuthoringToolId);
                Assert.Equal("1FGh4gRhLBFvhCiTUcE_$w", topic.Components[1].IfcGuid);
                Assert.Null(topic.Components[1].AuthoringToolId);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Reads_the_bcf_30_shape_where_one_viewpoints_element_wraps_several_viewpoint_children()
        {
            const string markup =
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Markup>
  <Topic Guid=""55555555-5555-5555-5555-555555555555"" TopicStatus=""Active"">
    <Title>Pipe vs Wall</Title>
  </Topic>
  <Viewpoints>
    <ViewPoint Guid=""66666666-6666-6666-6666-666666666666"">
      <Viewpoint>viewpoint.bcfv</Viewpoint>
      <Index>0</Index>
    </ViewPoint>
  </Viewpoints>
</Markup>";
            const string viewpoint =
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<VisualizationInfo Guid=""77777777-7777-7777-7777-777777777777"">
  <Components>
    <Selection>
      <Component>
        <OriginatingSystem>ACC</OriginatingSystem>
        <AuthoringToolId>222</AuthoringToolId>
      </Component>
      <Component>
        <OriginatingSystem>ACC</OriginatingSystem>
        <AuthoringToolId>333</AuthoringToolId>
      </Component>
    </Selection>
  </Components>
</VisualizationInfo>";
            string path = WriteZip(new Dictionary<string, string>
            {
                ["T/markup.bcf"] = markup,
                ["T/viewpoint.bcfv"] = viewpoint
            });
            try
            {
                List<BcfTopic> topics;
                string error;
                Assert.True(BcfMarkupReader.TryReadTopics(path, out topics, out error), error);
                Assert.Single(topics);
                BcfTopic topic = topics[0];
                Assert.Equal("Pipe vs Wall", topic.Title);
                Assert.Equal(2, topic.Components.Count);
                Assert.Equal("222", topic.Components[0].AuthoringToolId);
                Assert.Null(topic.Components[0].IfcGuid);
                Assert.Equal("333", topic.Components[1].AuthoringToolId);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_topic_whose_declared_viewpoint_file_is_missing_still_reads_with_zero_components()
        {
            const string markup =
@"<Markup>
  <Topic Guid=""88888888-8888-8888-8888-888888888888"" TopicStatus=""Open""><Title>No viewpoint file</Title></Topic>
  <Viewpoints Guid=""99999999-9999-9999-9999-999999999999""><Viewpoint>missing.bcfv</Viewpoint></Viewpoints>
</Markup>";
            string path = WriteZip(new Dictionary<string, string> { ["T/markup.bcf"] = markup });
            try
            {
                List<BcfTopic> topics;
                string error;
                Assert.True(BcfMarkupReader.TryReadTopics(path, out topics, out error), error);
                Assert.Single(topics);
                Assert.Empty(topics[0].Components);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_topic_whose_viewpoint_file_is_not_valid_xml_still_reads_with_zero_components()
        {
            const string markup =
@"<Markup>
  <Topic Guid=""aaaaaaaa-1111-1111-1111-111111111111"" TopicStatus=""Open""><Title>Broken viewpoint</Title></Topic>
  <Viewpoints Guid=""bbbbbbbb-1111-1111-1111-111111111111""><Viewpoint>viewpoint.bcfv</Viewpoint></Viewpoints>
</Markup>";
            string path = WriteZip(new Dictionary<string, string>
            {
                ["T/markup.bcf"] = markup,
                ["T/viewpoint.bcfv"] = "<not><valid"
            });
            try
            {
                List<BcfTopic> topics;
                string error;
                // ONE topic's broken viewpoint must not sink the whole import.
                Assert.True(BcfMarkupReader.TryReadTopics(path, out topics, out error), error);
                Assert.Single(topics);
                Assert.Empty(topics[0].Components);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_topic_with_no_guid_is_not_read_as_a_topic()
        {
            const string markup = @"<Markup><Topic TopicStatus=""Open""><Title>No guid</Title></Topic></Markup>";
            string path = WriteZip(new Dictionary<string, string> { ["T/markup.bcf"] = markup });
            try
            {
                List<BcfTopic> topics;
                string error;
                Assert.True(BcfMarkupReader.TryReadTopics(path, out topics, out error), error);
                Assert.Empty(topics);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_zip_with_no_markup_bcf_entry_reads_as_zero_topics_not_a_failure()
        {
            string path = WriteZip(new Dictionary<string, string> { ["readme.txt"] = "not a bcf" });
            try
            {
                List<BcfTopic> topics;
                string error;
                Assert.True(BcfMarkupReader.TryReadTopics(path, out topics, out error), error);
                Assert.Empty(topics);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_broken_markup_bcf_fails_the_whole_read_rather_than_import_half_a_file()
        {
            string path = WriteZip(new Dictionary<string, string> { ["T/markup.bcf"] = "<not><valid" });
            try
            {
                List<BcfTopic> topics;
                string error;
                Assert.False(BcfMarkupReader.TryReadTopics(path, out topics, out error));
                Assert.NotNull(error);
                Assert.Contains("not valid XML", error);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_file_that_is_not_a_zip_at_all_is_refused_by_name()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-bcf-test-notazip-" + System.Guid.NewGuid().ToString("N") + ".bcfzip");
            File.WriteAllText(path, "this is plain text, not a zip");
            try
            {
                List<BcfTopic> topics;
                string error;
                Assert.False(BcfMarkupReader.TryReadTopics(path, out topics, out error));
                Assert.Contains("zip", error);
            }
            finally { File.Delete(path); }
        }

        // ---- status mapping, comment identity, external-change dating -----------

        [Theory]
        [InlineData("Closed", CoordinationRules.StatusClosedByDecision)]
        [InlineData("Resolved", CoordinationRules.StatusClosedByDecision)]
        [InlineData("Open", CoordinationRules.StatusOpen)]
        [InlineData("Active", CoordinationRules.StatusOpen)]
        [InlineData("ReOpened", CoordinationRules.StatusOpen)]
        [InlineData("SomeCustomStatus", null)]
        [InlineData(null, null)]
        public void MapStatus_maps_the_bcf_vocabulary_never_to_resolved_by_model(string bcfStatus, string expected)
        {
            Assert.Equal(expected, BcfMarkupReader.MapStatus(bcfStatus));
        }

        [Fact]
        public void LastExternalChange_is_the_newest_comment_date_or_the_creation_date_with_none()
        {
            var topic = new BcfTopic { CreationDate = "2026-01-01T00:00:00Z" };
            Assert.Equal("2026-01-01T00:00:00Z", BcfMarkupReader.LastExternalChange(topic));

            topic.Comments.Add(new BcfComment { Date = "2026-01-05T00:00:00Z" });
            topic.Comments.Add(new BcfComment { Date = "2026-01-03T00:00:00Z" });
            Assert.Equal("2026-01-05T00:00:00Z", BcfMarkupReader.LastExternalChange(topic));
        }

        [Fact]
        public void LastExternalChange_of_a_topic_with_no_date_at_all_is_null()
        {
            Assert.Null(BcfMarkupReader.LastExternalChange(new BcfTopic()));
        }

        [Fact]
        public void AlreadyRecorded_stops_a_reimported_comment_from_duplicating()
        {
            var finding = new CoordinationFinding { Id = "f1" };
            var comment = new BcfComment { Author = "a", Date = "2026-01-01T00:00:00Z", Text = "hi" };
            Assert.False(BcfMarkupReader.AlreadyRecorded(finding, comment));

            CoordinationRules.AppendEvent(finding, "comment", BcfMarkupReader.ImportedCommentText(comment), "2026-01-02T00:00:00Z");
            Assert.True(BcfMarkupReader.AlreadyRecorded(finding, comment));
        }
    }
}
