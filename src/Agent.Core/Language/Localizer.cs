using System.Globalization;

namespace Agent.Core.Language;

public enum MessageKey
{
    Greeting,
    RateLimited,
    RepeatedMessage,
    SensitiveData,
    LlmFailure,
    TurnTimeout,
    EmptyReply,
    TruncatedReply,
    TooManySteps,
    Escalated,
    FormAccepted,
}

/// <summary>Canned user-facing text for the two supported locales. Unknown locales fall back to English.</summary>
public sealed class Localizer
{
    public string Get(MessageKey key, string locale, params object[] args)
    {
        var template = locale == "ar" ? Arabic(key) : English(key);
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    private static string English(MessageKey key) => key switch
    {
        MessageKey.Greeting => "Hi, I'm {0}. I can help you search for and book flights and hotels. How can I help?",
        MessageKey.RateLimited => "You are sending messages too quickly. Please wait a moment and try again.",
        MessageKey.RepeatedMessage => "You have sent the same message several times. Please rephrase it or tell me something new so I can help.",
        MessageKey.SensitiveData => "For your security, please do not share card, account or ID numbers in chat. I have removed it from the conversation. Payment and identity details are collected through secure forms.",
        MessageKey.LlmFailure => "Sorry, something went wrong on my side. Please try again in a moment.",
        MessageKey.TurnTimeout => "That took longer than expected, so I stopped. Please try again.",
        MessageKey.EmptyReply => "Sorry, I could not come up with an answer. Could you rephrase your request?",
        MessageKey.TruncatedReply => "My answer was cut short. Could you ask for a smaller piece of this, or rephrase?",
        MessageKey.TooManySteps => "I could not finish that in one go. Tell me what you would like to do next and I will continue.",
        MessageKey.Escalated => "Your conversation has been passed to a human agent. They will follow up with you shortly.",
        MessageKey.FormAccepted => "Thank you, your details are saved. Choose a payment method, then ask me to complete the booking.",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    private static string Arabic(MessageKey key) => key switch
    {
        MessageKey.Greeting => "مرحباً، أنا {0}. يمكنني مساعدتك في البحث عن رحلات الطيران والفنادق وحجزها. كيف يمكنني مساعدتك؟",
        MessageKey.RateLimited => "ترسل الرسائل بسرعة كبيرة. يرجى الانتظار لحظة ثم المحاولة مرة أخرى.",
        MessageKey.RepeatedMessage => "لقد أرسلت الرسالة نفسها عدة مرات. يرجى إعادة صياغتها أو إخباري بشيء جديد حتى أتمكن من مساعدتك.",
        MessageKey.SensitiveData => "حفاظاً على أمانك، يرجى عدم مشاركة أرقام البطاقات أو الحسابات أو الهوية في المحادثة. لقد أزلتها من المحادثة. تُجمع بيانات الدفع والهوية عبر نماذج آمنة.",
        MessageKey.LlmFailure => "عذراً، حدث خطأ من جانبي. يرجى المحاولة مرة أخرى بعد قليل.",
        MessageKey.TurnTimeout => "استغرق الأمر وقتاً أطول من المتوقع فتوقفت. يرجى المحاولة مرة أخرى.",
        MessageKey.EmptyReply => "عذراً، لم أتمكن من إيجاد إجابة. هل يمكنك إعادة صياغة طلبك؟",
        MessageKey.TruncatedReply => "تم اقتطاع إجابتي. هل يمكنك طلب جزء أصغر أو إعادة الصياغة؟",
        MessageKey.TooManySteps => "لم أتمكن من إنهاء ذلك دفعة واحدة. أخبرني بما تريد فعله بعد ذلك وسأتابع.",
        MessageKey.Escalated => "تمت إحالة محادثتك إلى موظف بشري وسيتواصل معك قريباً.",
        MessageKey.FormAccepted => "شكراً لك، تم حفظ بياناتك. اختر طريقة الدفع ثم اطلب مني إتمام الحجز.",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
