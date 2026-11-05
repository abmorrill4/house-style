using System;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class AssistTests
    {
        [Fact]
        public void TopicAssistParsesEverySection()
        {
            var a = TopicAssist.Parse(@"{""description"":""Pay a bill from Digital Banking."",""keywords"":[""bill pay"",""payments:scheduling""],
                ""synonyms"":[[""payee"",""biller"",""recipient""],[""lonely""]],""micro_content"":[{""phrase"":""How do I pay a bill?"",""response"":""Select Pay.""}],
                ""glossary"":[{""term"":""payee"",""definition"":""The person or company you pay.""}]}");
            Assert.Equal("Pay a bill from Digital Banking.", a.Description);
            Assert.Equal(2, a.Keywords.Count);
            Assert.Single(a.Synonyms);                       // a one-word "group" is dropped
            Assert.Equal("How do I pay a bill?", a.MicroContent[0].Key);
            Assert.Equal("payee", a.Glossary[0].Key);
        }

        [Fact]
        public void AltTextIsAppliedAsOneValidatedBlockReplacement()
        {
            var dir = Path.Combine(Path.GetTempPath(), "alt-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(dir, "Images"));
            try
            {
                var topic = Path.Combine(dir, "t.htm");
                File.WriteAllText(topic, "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body>" +
                                         "<h2>Pay a bill</h2><p>The payee list: <img src=\"Images/payees.png\" /></p></body></html>");
                File.WriteAllBytes(Path.Combine(dir, "Images", "payees.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                var doc = new FakeDocumentAdapter(topic, true);
                var block = doc.Document.SelectSingleNode("//*[local-name()='p']");
                var img = AltText.MissingIn(block);
                Assert.NotNull(img);
                var file = AltText.Load(topic, img.GetAttribute("src"));
                Assert.Equal("image/png", file.MediaType);
                Assert.Contains("Heading: Pay a bill", AltText.Context(block));
                AltText.Apply(doc, block, "Images/payees.png", "The Payees list with three payees");
                var after = doc.Document.SelectSingleNode("//*[local-name()='img']") as XmlElement;
                Assert.Equal("The Payees list with three payees", after.GetAttribute("alt"));
                Assert.Throws<InvalidOperationException>(() => AltText.Load(topic, "Images/diagram.svg"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PdfsAreSentAsDocumentsBeforeTheText()
        {
            var docs = new System.Collections.Generic.List<LlmImage> { new LlmImage { MediaType = "application/pdf", Base64 = "JVBE" } };
            var body = AnthropicClient.BuildBody("m", new LlmRequest { User = "Read", Documents = docs });
            Assert.True(body.IndexOf("\"type\":\"document\"", StringComparison.Ordinal) < body.IndexOf("\"text\":\"Read\"", StringComparison.Ordinal));
            Assert.Contains("\"media_type\":\"application/pdf\"", body);
            var bifrost = BifrostClient.BuildBody("m", new LlmRequest { User = "Read", Documents = docs });
            Assert.Contains("\"type\":\"file\"", bifrost);
            Assert.Contains("data:application/pdf;base64,JVBE", bifrost);
        }

        [Fact]
        public void ImagesAreSentBeforeTheText()
        {
            var body = AnthropicClient.BuildBody("m", new LlmRequest { User = "Describe", Images = new System.Collections.Generic.List<LlmImage> { new LlmImage { MediaType = "image/png", Base64 = "AAAA" } } });
            Assert.True(body.IndexOf("\"type\":\"image\"", StringComparison.Ordinal) < body.IndexOf("\"text\":\"Describe\"", StringComparison.Ordinal));
            Assert.Contains("\"media_type\":\"image/png\"", body);
            var bifrost = BifrostClient.BuildBody("m", new LlmRequest { User = "Describe", Images = new System.Collections.Generic.List<LlmImage> { new LlmImage { MediaType = "image/png", Base64 = "AAAA" } } });
            Assert.Contains("data:image/png;base64,AAAA", bifrost);
        }

        [Fact]
        public void GatewaySendsTheClaude55ModelOfTheSameFamily()
        {
            Assert.Equal("anthropic/claude-haiku-5-5", BifrostClient.GatewayModel("claude-haiku-4-5"));
            Assert.Equal("anthropic/claude-opus-5-5", BifrostClient.GatewayModel("claude-opus-5-5"));
            Assert.Equal("anthropic/claude-sonnet-5-5", BifrostClient.GatewayModel(null));
            Assert.Contains("\"model\":\"anthropic/claude-sonnet-5-5\"", BifrostClient.BuildBody("claude-sonnet-5-5", new LlmRequest { User = "x" }));
        }
    }
}
