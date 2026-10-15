using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;

namespace AIWorld.Core.Chat;

public static class PlayerChatRules
{
    // Whole-message controls only. Questions, negations and quoted examples remain dialogue.
    private static string ControlText(string text) => text.Trim().TrimEnd('。', '.', '!', '！').Trim().ToLowerInvariant();

    public static bool IsConfirm(string text) => ControlText(text) is "确认" or "确认约会" or "确认这个约会" or "确认新条件"
        or "確認" or "確認約會" or "確認新條件" or "confirm" or "confirm appointment" or "確定" or "確認する" or "확인" or "confirmar";

    public static bool IsCancelAppointment(string text) => ControlText(text) is "取消约会" or "取消这次约会" or "cancel appointment"
        or "取消約會" or "取消這次約會" or "約束をキャンセル" or "約束を取り消す" or "약속 취소" or "cancelar cita" or "cancelar la cita";

    public static bool IsStopFollowing(string text, bool following) => ControlText(text) is
        "停止跟随" or "别跟着我了" or "不用跟着我了" or "不用跟了" or "你先回去吧" or
        "stop following" or "stop following me" or "停止跟隨" or "不用跟著我了" or "不用跟了"
        or "ついてこないで" or "追従をやめて" or "따라오지 마" or "따라오기 중지" or "deja de seguirme"
        || following && ControlText(text) is "算了" or "不去了" or "never mind";

    public static string ConsentPrompt(PlayerRequest request)
    {
        var binding = JsonSerializer.Serialize(new
        {
            requestId = request.RequestId, npcId = request.NpcId, termsHash = request.TermsHash,
            target = request.GroundedTarget, day = request.RequestedGameDay,
            time = request.RequestedGameTime, kind = request.RequestKind.ToString()
        });
        return "\nPLAYER REQUEST (validated terms, not an order):\n" + binding + "\n"
            + "Decide freely using your personality, relationship, current activity and schedule. "
            + "ACCEPT only if you willingly agree; DECLINE if you refuse; UNCLEAR if the player is only asking a question or the request is ambiguous; "
            + "COUNTERPROPOSE if you suggest other terms. Never infer acceptance from the player's request alone.\n"
            + "Your full response must include decision with EXACT requestId, npcId and termsHash from this binding, "
            + "outcome (ACCEPT|DECLINE|UNCLEAR|COUNTERPROPOSE), proposedTerms (string or null), flavorDialogue (string or null), schemaVersion=\"1\". "
            + "Keep speech consistent with the outcome. intent MUST be null: this decision is the only authority for travel or an appointment. "
            + "Keep memoryCandidates and stateDelta empty. This request rule overrides the earlier follow-intent examples. "
            + "For counterproposals include an explicit location and numeric time in proposedTerms. Use the configured OUTPUT LANGUAGE.\n";
    }

    public static bool CanDispatchDirectIntent(string type, bool hasBoundRequest) =>
        !hasBoundRequest && type is CapabilityRegistry.CapabilityIds.FollowPlayer
            or CapabilityRegistry.CapabilityIds.StopFollowing
            or CapabilityRegistry.CapabilityIds.MagicInvestigate
            or CapabilityRegistry.CapabilityIds.MagicLearn;
}
