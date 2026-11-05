using System;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class SafeXmlTests
    {
        private const string Bomb =
            "<?xml version=\"1.0\"?><!DOCTYPE html [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
            "<html><body><p>&b;</p></body></html>";

        private static string Temp(string name, string content)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hs-safexml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name);
            File.WriteAllText(file, content);
            return file;
        }

        [Fact]
        public void A_file_that_declares_entities_is_not_expanded()
        {
            var file = Temp("bomb.htm", Bomb);
            Assert.Throws<XmlException>(() => SafeXml.Load(file));
        }

        [Fact]
        public void A_string_that_declares_entities_is_not_expanded()
        {
            Assert.Throws<XmlException>(() => SafeXml.LoadXml(Bomb));
        }

        [Fact]
        public void A_doctype_with_no_entity_use_still_loads()
        {
            var file = Temp("plain.htm", "<?xml version=\"1.0\"?><!DOCTYPE html [<!ENTITY a \"x\">]><html><body><p>Hi</p></body></html>");
            var doc = SafeXml.Load(file);
            Assert.Equal("Hi", doc.SelectSingleNode("//p").InnerText);
        }

        [Fact]
        public void Real_flare_topic_loads_and_keeps_whitespace_when_asked()
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body>\n  <p MadCap:conditions=\"a.b\">Hi <b>there</b></p>\n</body></html>";
            var file = Temp("t.htm", xml);
            var kept = SafeXml.Load(file, true);
            var dropped = SafeXml.Load(file);
            Assert.Contains("\n  <p", kept.OuterXml);
            Assert.DoesNotContain("\n  <p", dropped.OuterXml);
            Assert.Equal("Hi there", kept.SelectSingleNode("//p").InnerText);
        }

        [Fact]
        public void The_settings_prohibit_external_resolving()
        {
            var s = SafeXml.Settings();
            Assert.NotEqual(DtdProcessing.Parse, s.DtdProcessing);
        }

        [Fact]
        public void A_guide_topic_with_an_entity_bomb_is_reported_not_expanded()
        {
            var root = Path.Combine(Path.GetTempPath(), "hs-safexml-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(root, "Content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "bad.htm"), Bomb);
            var layer = GuideLayer.Load(root, "test");
            Assert.Contains(layer.Problems, p => p.Message.StartsWith("Not well-formed XML"));
        }
    }
}
