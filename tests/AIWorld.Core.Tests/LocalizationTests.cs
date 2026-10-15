using System.Text.RegularExpressions;
using AIWorld.Core.Chat;
using Xunit;

namespace AIWorld.Core.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("zh", "zh-CN")][InlineData("zh-CN", "zh-CN")][InlineData("zh_Hans_CN", "zh-CN")]
    [InlineData("zh_TW", "zh-TW")][InlineData("zh-HK", "zh-TW")][InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("Traditional_Chinese", "zh-TW")][InlineData("繁體中文", "zh-TW")]
    [InlineData("ja-JP", "ja")][InlineData("ko-KR", "ko")][InlineData("es-ES", "es")][InlineData("es-MX", "es")]
    [InlineData("en", "en")][InlineData("en-US", "en")][InlineData("unknown-pack", "en")]
    public void ResolvesBuiltInAndCustomLanguageCodes(string gameCode, string expected)
        => Assert.Equal(expected, UiText.Resolve(gameCode));

    [Fact] public void SixCatalogsHaveIdenticalKeysAndFormatArguments()
    {
        var english = UiText.Catalog("en"); Assert.True(english.Count >= 100);
        string[] Args(string text) => Regex.Matches(text, @"\{\d+(?:[^}]*)?\}").Select(m => m.Value).OrderBy(s => s).ToArray();
        foreach (var locale in UiText.Supported)
        {
            var catalog = UiText.Catalog(locale);
            Assert.Equal(english.Keys.OrderBy(s => s), catalog.Keys.OrderBy(s => s));
            foreach (var key in english.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(catalog[key]));
                Assert.Equal(Args(english[key]), Args(catalog[key]));
                Assert.DoesNotContain("{0}", UiText.Get(locale, key, "a", "b", "c"));
            }
        }
    }
    [Theory]
    [InlineData("en", "Conversation cancelled or failed.")]
    [InlineData("zh-CN", "对话取消或失败。")]
    [InlineData("zh-TW", "對話取消或失敗。")]
    [InlineData("ja", "会話がキャンセルされたか、失敗しました。")]
    [InlineData("ko", "대화가 취소되었거나 실패했습니다.")]
    [InlineData("es", "Conversación cancelada o fallida.")]
    public void UiAndFallbackResponsesAreTranslated(string locale, string expected)
        => Assert.Equal(expected, UiText.Get(locale, "ui.chat-failed"));

    [Fact] public void LegacyDefaultBecomesLanguageNeutralButCustomTextIsPreserved()
    {
        Assert.Equal(UiText.Get("ja", "prompt.default"), UiText.EffectivePrompt(TalkOptions.LegacyDefaultPrompt, "ja"));
        Assert.Equal(UiText.Get("es", "prompt.default"), UiText.EffectivePrompt(new TalkOptions().GlobalPrompt, "es"));
        const string custom = "使用简体中文。保留我的自定义风格";
        Assert.Equal(custom, UiText.EffectivePrompt(custom, "ko"));
        Assert.Contains("overrides", UiText.OutputInstruction("ko"));
    }
    [Theory]
    [InlineData("en")][InlineData("zh-CN")][InlineData("zh-TW")][InlineData("ja")][InlineData("ko")][InlineData("es")]
    public void ReportChangesHeadingsButNeverRewritesOriginalEvidence(string locale)
    {
        var record = new TalkRecord { Speech = "原话 그대로 original", Attempts = new[] {
            new TalkAttempt("model", "endpoint", "原始提示詞", "原始输入", "PAYLOAD", "RAW RESPONSE 原文", "Success", 200, 12, null, null, null) } };
        var report = TalkJournal.Report(record, locale);
        Assert.Contains(UiText.Get(locale, "report.title"), report);
        Assert.Contains(UiText.Get(locale, "report.unknown"), report);
        Assert.Contains(record.Speech, report); Assert.Contains("原始提示詞", report); Assert.Contains("RAW RESPONSE 原文", report);
        Assert.Equal("原话 그대로 original", record.Speech);
    }
    [Theory]
    [InlineData("确认")][InlineData("確認")][InlineData("confirm")][InlineData("確認する")][InlineData("확인")][InlineData("confirmar")]
    public void TranslatedConfirmationHintsHaveMatchingProgrammaticControls(string text) => Assert.True(PlayerChatRules.IsConfirm(text));
    [Theory]
    [InlineData("取消约会", "停止跟随")][InlineData("取消約會", "停止跟隨")][InlineData("cancel appointment", "stop following me")]
    [InlineData("約束をキャンセル", "ついてこないで")][InlineData("약속 취소", "따라오지 마")][InlineData("cancelar cita", "deja de seguirme")]
    public void ExplicitLocalizedCommandsRemainStrict(string cancel, string stop)
    {
        Assert.True(PlayerChatRules.IsCancelAppointment(cancel)); Assert.True(PlayerChatRules.IsStopFollowing(stop, true));
        Assert.False(PlayerChatRules.IsCancelAppointment(cancel + "?")); Assert.False(PlayerChatRules.IsStopFollowing(stop + "?", true));
        Assert.False(PlayerChatRules.IsConfirm("\"confirm\""));
    }
}
