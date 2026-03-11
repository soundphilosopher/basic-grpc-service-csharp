using System.Text.RegularExpressions;

namespace Utils;

/// <summary>
/// A C# implementation of the classic ELIZA chatbot using the DOCTOR script.
/// </summary>
/// <remarks>
/// <para>
/// ELIZA was originally created by Joseph Weizenbaum at MIT (1964–1966) as an experiment
/// in natural-language processing. This implementation follows the DOCTOR script — the most
/// well-known variant — which simulates a Rogerian psychotherapist by reflecting the user's
/// own statements back as open-ended questions.
/// </para>
/// <para>
/// The algorithm works in five steps on every call to <see cref="Reply"/>:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Scan</b> — the normalised input is scanned for every keyword defined in
///     <see cref="BuildScript"/>. Matching keywords are sorted by descending priority.
///   </description></item>
///   <item><description>
///     <b>Decompose</b> — for the highest-priority keyword, each <see cref="DecompRule"/>
///     is tried in order until a pattern matches the full input.
///   </description></item>
///   <item><description>
///     <b>Reassemble</b> — <see cref="Assemble"/> picks the next reassembly template for that
///     rule (cycling through them so repeated inputs produce varied replies) and substitutes
///     <c>(1)</c>, <c>(2)</c>, … placeholders with the corresponding regex capture groups.
///   </description></item>
///   <item><description>
///     <b>Reflect</b> — <see cref="Reflect"/> transforms pronouns inside captured groups
///     (e.g. <c>"I am"</c> → <c>"you are"</c>) so echoed phrases read naturally.
///   </description></item>
///   <item><description>
///     <b>Fallback</b> — when no keyword matches, a queued memory response is recalled
///     (seeded earlier by <c>"my …"</c> matches), or a generic prompt from
///     <see cref="_genericFallbacks"/> is returned at random.
///   </description></item>
/// </list>
/// </remarks>
public sealed partial class Eliza
{
    // ── Reflection table ──────────────────────────────────────────────────

    /// <summary>
    /// Maps first-person words to their second-person equivalents (and vice-versa for
    /// a small set of common forms) so that captured phrases can be echoed back
    /// naturally — e.g. <c>"I am sad"</c> becomes <c>"you are sad"</c>.
    /// </summary>
    /// <remarks>
    /// Lookups are case-insensitive. Only whole words are ever passed to this table
    /// (see <see cref="Reflect"/>), so short entries such as <c>"i"</c> will not
    /// accidentally match substrings.
    /// </remarks>
    private static readonly Dictionary<string, string> Reflections =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["am"] = "are",
            ["was"] = "were",
            ["i"] = "you",
            ["i'd"] = "you would",
            ["i've"] = "you have",
            ["i'll"] = "you will",
            ["my"] = "your",
            ["are"] = "am",
            ["you've"] = "I have",
            ["you'll"] = "I will",
            ["your"] = "my",
            ["yours"] = "mine",
            ["you"] = "me",
            ["me"] = "you",
        };

    // ── Internal data model ───────────────────────────────────────────────

    /// <summary>
    /// A single decomposition rule consisting of a compiled <see cref="Regex"/> pattern
    /// and an ordered list of reassembly templates to cycle through on successive matches.
    /// </summary>
    /// <remarks>
    /// Templates reference regex capture groups with one-based numeric placeholders:
    /// <c>(1)</c> expands to group 1, <c>(2)</c> to group 2, and so on.
    /// Each placeholder value is passed through <see cref="Reflect"/> before insertion.
    /// </remarks>
    private sealed record DecompRule(Regex Pattern, string[] Reassemblies);

    /// <summary>
    /// A single entry in the DOCTOR script, binding a trigger word or phrase to a
    /// numeric priority and one or more <see cref="DecompRule">decomposition rules</see>.
    /// </summary>
    /// <remarks>
    /// When multiple keywords are found in the same input, the one with the highest
    /// <c>Priority</c> value is processed first. If its rules all fail to match,
    /// the next keyword by priority is tried.
    /// </remarks>
    private sealed record Keyword(string Word, int Priority, DecompRule[] Rules);

    // ── State ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The compiled DOCTOR script, populated once by <see cref="BuildScript"/>
    /// during construction and treated as read-only thereafter.
    /// </summary>
    private readonly Keyword[] _script;

    /// <summary>Random number generator used to select from <see cref="_genericFallbacks"/>.</summary>
    private readonly Random _rng = new();

    /// <summary>
    /// FIFO queue of responses assembled from <c>"my …"</c> keyword matches.
    /// Entries are enqueued during normal processing and dequeued as fallback
    /// replies when no keyword matches a subsequent input, grounding the
    /// conversation in something the user previously said.
    /// </summary>
    private readonly Queue<string> _memory = new();

    /// <summary>
    /// Maps each <see cref="DecompRule"/> to the number of times it has fired.
    /// <see cref="Assemble"/> uses this count modulo the reassembly list length to
    /// cycle through templates instead of always returning the first one.
    /// </summary>
    private readonly Dictionary<DecompRule, int> _fireCount = new();

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Initialises ELIZA and compiles the DOCTOR script by calling <see cref="BuildScript"/>.
    /// </summary>
    public Eliza() => _script = BuildScript();

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Produces ELIZA's reply to a single user message.
    /// </summary>
    /// <remarks>
    /// The input is first normalised (see <see cref="Normalize"/>), then matched against
    /// the DOCTOR script. Empty or whitespace-only input bypasses the script and returns
    /// a generic continuation prompt immediately. The internal <see cref="_memory"/> queue
    /// and <see cref="_fireCount"/> table are mutated as a side-effect of each call,
    /// so this method is <b>not</b> thread-safe.
    /// </remarks>
    /// <param name="input">Raw user message; may contain any punctuation or casing.</param>
    /// <returns>
    /// A non-empty reply string. The reply is one of:
    /// <list type="bullet">
    ///   <item><description>A reassembled response from the first matching <see cref="DecompRule"/>.</description></item>
    ///   <item><description>A previously stored memory response (from an earlier <c>"my …"</c> match).</description></item>
    ///   <item><description>A randomly selected entry from <see cref="_genericFallbacks"/>.</description></item>
    /// </list>
    /// </returns>
    public string Reply(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Please go on.";

        var normalized = Normalize(input);

        // Collect every keyword present in the input and sort highest priority first.
        var active = _script
            .Where(k => ContainsKeyword(normalized, k.Word))
            .OrderByDescending(k => k.Priority);

        foreach (var keyword in active)
        {
            foreach (var rule in keyword.Rules)
            {
                var match = rule.Pattern.Match(normalized);
                if (!match.Success)
                    continue;

                var response = Assemble(rule, match);

                // Tuck away responses from "my …" branches as future memory.
                if (keyword.Word == "my")
                    _memory.Enqueue(response);

                return response;
            }
        }

        // Nothing matched — recall a stored memory or fall back to a generic prompt.
        if (_memory.Count > 0)
            return _memory.Dequeue();

        return _genericFallbacks[_rng.Next(_genericFallbacks.Length)];
    }

    // ── Private helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Prepares raw user input for pattern matching by trimming surrounding whitespace,
    /// collapsing internal runs of whitespace to a single space, and removing trailing
    /// punctuation characters.
    /// </summary>
    /// <remarks>
    /// Trailing punctuation is stripped because decomposition patterns match on words and
    /// phrases, not sentence-ending characters — leaving a period or question mark would
    /// prevent patterns like <c>i feel (.*)</c> from matching <c>"i feel sad."</c>.
    /// </remarks>
    /// <param name="input">The raw string as received from the caller.</param>
    /// <returns>A normalised copy of <paramref name="input"/>; the original is not modified.</returns>
    private static string Normalize(string input)
    {
        input = input.Trim();
        input = WhitespaceRegex().Replace(input, " ");
        input = TrailingPunctuationRegex().Replace(input, "");
        return input;
    }

    /// <summary>Matches one or more consecutive whitespace characters.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Matches one or more trailing punctuation characters (<c>! ? . ; : ,</c>) at the end of a string.</summary>
    [GeneratedRegex(@"[!?.;:,]+$")]
    private static partial Regex TrailingPunctuationRegex();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="keyword"/> appears in
    /// <paramref name="input"/> as a whole-word (or whole-phrase) match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard <c>\b</c> word boundaries are not used because they do not behave
    /// correctly for multi-word keywords such as <c>"i feel"</c> (the space inside
    /// the phrase is not a word boundary). Instead, negative lookbehind
    /// <c>(?&lt;![a-z])</c> and negative lookahead <c>(?![a-z])</c> ensure that
    /// neither end of the keyword is immediately adjacent to another letter.
    /// </para>
    /// </remarks>
    /// <param name="input">The normalised input string.</param>
    /// <param name="keyword">The keyword or phrase to search for, e.g. <c>"i feel"</c>.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="keyword"/> is found as a whole-word match;
    /// otherwise <see langword="false"/>.
    /// </returns>
    private static bool ContainsKeyword(string input, string keyword) =>
        Regex.IsMatch(input, $@"(?<![a-z]){Regex.Escape(keyword)}(?![a-z])",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// Selects the next reassembly template for <paramref name="rule"/>, substitutes
    /// all <c>(N)</c> placeholders with the corresponding reflected capture groups from
    /// <paramref name="match"/>, and returns the finished response string.
    /// </summary>
    /// <remarks>
    /// Template cycling is handled by incrementing the rule's counter in
    /// <see cref="_fireCount"/> on every call. The counter is taken modulo the
    /// number of available templates so it wraps around indefinitely, ensuring
    /// variety without repetition until the full list has been exhausted.
    /// </remarks>
    /// <param name="rule">The matched decomposition rule whose templates are cycled through.</param>
    /// <param name="match">The successful <see cref="Match"/> produced by <paramref name="rule"/>'s pattern.</param>
    /// <returns>The assembled response string with all placeholders resolved.</returns>
    private string Assemble(DecompRule rule, Match match)
    {
        var index = _fireCount.GetValueOrDefault(rule, 0);
        var template = rule.Reassemblies[index % rule.Reassemblies.Length];
        _fireCount[rule] = index + 1;

        return PlaceholderRegex().Replace(template, m =>
        {
            var groupIndex = int.Parse(m.Groups[1].Value);
            return groupIndex < match.Groups.Count
                ? Reflect(match.Groups[groupIndex].Value.Trim())
                : string.Empty;
        });
    }

    /// <summary>Matches a numeric placeholder of the form <c>(N)</c>, capturing the digit(s) in group 1.</summary>
    [GeneratedRegex(@"\((\d+)\)")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Applies word-level reflection to a captured phrase so that it reads naturally
    /// when echoed back to the user.
    /// </summary>
    /// <remarks>
    /// Each word is looked up independently in <see cref="Reflections"/>. Words that
    /// have no entry are left unchanged, so only pronouns and common auxiliary verbs
    /// are transformed (e.g. <c>"I am tired"</c> → <c>"you are tired"</c>,
    /// <c>"my cat"</c> → <c>"your cat"</c>).
    /// </remarks>
    /// <param name="phrase">A single captured group value, already trimmed of surrounding whitespace.</param>
    /// <returns>The phrase with applicable words reflected; all other words are preserved as-is.</returns>
    private static string Reflect(string phrase)
    {
        var words = phrase.Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (Reflections.TryGetValue(words[i], out var reflected))
                words[i] = reflected;
        return string.Join(' ', words);
    }

    /// <summary>
    /// Responses returned at random when no keyword in the DOCTOR script matches the
    /// current input and <see cref="_memory"/> is empty. Designed to be open-ended
    /// so that they prompt the user to continue without implying any specific topic.
    /// </summary>
    private static readonly string[] _genericFallbacks =
    [
        "Please go on.",
        "Tell me more.",
        "Can you elaborate on that?",
        "I see. Please continue.",
        "What does that suggest to you?",
        "Do you feel strongly about discussing such things?",
        "That is quite interesting.",
        "I'm not sure I fully understand you.",
        "Does talking about this help you in any way?",
        "Can you be more specific?",
    ];

    // ── DOCTOR script ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds and returns the full DOCTOR keyword script as an array of
    /// <see cref="Keyword"/> entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keywords are evaluated in descending priority order during <see cref="Reply"/>.
    /// The priority values used here follow the spirit of Weizenbaum's original
    /// weighting, where highly specific or emotionally charged words rank highest:
    /// </para>
    /// <list type="table">
    ///   <listheader><term>Priority</term><description>Keywords</description></listheader>
    ///   <item><term>50</term><description><c>computer</c> — direct reference to ELIZA's own domain.</description></item>
    ///   <item><term>20</term><description><c>dream</c> — psychologically significant.</description></item>
    ///   <item><term>15</term><description><c>name</c>, <c>mother</c>, <c>father</c>, <c>family</c> — personal relationships.</description></item>
    ///   <item><term>8</term><description><c>i feel</c>, <c>i want</c>, <c>i need</c> — specific first-person phrases.</description></item>
    ///   <item><term>5</term><description><c>hello</c>, <c>goodbye</c>, <c>i am</c>, <c>you</c>.</description></item>
    ///   <item><term>1–4</term><description>General words: <c>i</c>, <c>my</c>, <c>yes</c>, <c>no</c>, <c>why</c>, <c>maybe</c>, etc.</description></item>
    /// </list>
    /// <para>
    /// Within each keyword, <see cref="DecompRule"/> entries are ordered
    /// most-specific first. The first rule whose pattern matches the input is used;
    /// remaining rules for that keyword are skipped.
    /// </para>
    /// </remarks>
    /// <returns>A freshly allocated array of <see cref="Keyword"/> entries representing the DOCTOR script.</returns>
    private static Keyword[] BuildScript() =>
    [
        // ── Greetings / closings ──────────────────────────────────────────
        K("hello", 10,
            Rule(@"(.*)",
                "How do you do. Please state your problem.",
                "Hi there. What seems to be on your mind today?",
                "Hello. How are you feeling?")),

        K("goodbye", 10,
            Rule(@"(.*)",
                "Goodbye. It was nice talking with you.",
                "Goodbye! I hope I've been of some help.",
                "Take care. See you next time.")),

        // ── High-signal content words ─────────────────────────────────────
        K("computer", 50,
            Rule(@"(.*)",
                "Do computers worry you?",
                "Why do you mention computers?",
                "What do you think machines have to do with your problem?",
                "Don't you think computers can help people?",
                "What about machines worries you?")),

        K("dream", 20,
            Rule(@"(.*)",
                "What does that dream suggest to you?",
                "Do you dream often?",
                "What persons appear in your dreams?",
                "Do you believe dreams are related to your problem?")),

        K("family", 15,
            Rule(@"(.*)",
                "Tell me more about your family.",
                "How does your family make you feel?",
                "What else comes to mind when you think of your family?")),

        K("mother", 15,
            Rule(@"(.*)",
                "Tell me more about your mother.",
                "What was your relationship with your mother like?",
                "How do you feel about your mother?",
                "How does this relate to your feelings today?")),

        K("father", 15,
            Rule(@"(.*)",
                "Tell me more about your father.",
                "How did your father make you feel?",
                "Does your relationship with your father affect your feelings today?",
                "Do you have trouble showing affection with your family?")),

        K("name", 15,
            Rule(@"(.*)",
                "I am not interested in names.",
                "I've told you before, I don't care about names.",
                "Please don't mention names here.")),

        // ── "I …" branches (ordered most-specific → least-specific) ───────
        K("i feel", 8,
            Rule(@"i feel (.*)",
                "Tell me more about feeling (1).",
                "Do you often feel (1)?",
                "What makes you feel (1)?",
                "How long have you been feeling (1)?")),

        K("i want", 8,
            Rule(@"i want (.*)",
                "What would it mean to you if you got (1)?",
                "Why do you want (1)?",
                "Suppose you soon got (1) — how would that feel?",
                "What would you do if you got (1)?",
                "Is it really (1) that you want?")),

        K("i need", 8,
            Rule(@"i need (.*)",
                "Why do you need (1)?",
                "Would it really help you to get (1)?",
                "Are you sure you need (1)?",
                "What if you didn't have (1)?")),

        K("i think", 6,
            Rule(@"i think (.*)",
                "Do you really think so?",
                "But you're not entirely sure you (1)?",
                "Do you doubt that you (1)?",
                "What makes you think (1)?")),

        K("i am", 5,
            Rule(@"i am (.*) because (.*)",
                "Is that the real reason?",
                "Don't any other reasons come to mind?",
                "Does that reason explain anything else?",
                "What other reasons might there be?"),
            Rule(@"i am (.*)",
                "How long have you been (1)?",
                "Do you believe it is normal to be (1)?",
                "How do you feel about being (1)?",
                "Why do you think you are (1)?")),

        K("i", 1,
            Rule(@"i (.*)",
                "You say you (1)?",
                "Can you elaborate on that?",
                "Do you really think you (1)?")),

        // ── Possessives ───────────────────────────────────────────────────
        K("my", 3,
            Rule(@"my (.*?) (is|was|are|were) (.*)",
                "Is it important to you that your (1) (2) (3)?",
                "Tell me more about your (1).",
                "What does it mean that your (1) (2) (3)?"),
            Rule(@"my (.*)",
                "Your (1), you say.",
                "Tell me more about your (1).",
                "Why does your (1) matter so much to you?",
                "Does anyone else in your life know about your (1)?")),

        // ── "You …" branches ──────────────────────────────────────────────
        K("you", 5,
            Rule(@"you are (.*)",
                "What makes you think I am (1)?",
                "Why do you think I am (1)?",
                "Does it please you to believe I am (1)?"),
            Rule(@"you (.*) me",
                "Why do you think I (1) you?",
                "What makes you feel that I (1) you?"),
            Rule(@"you (.*)",
                "We were discussing you, not me.",
                "Oh, I (1)?",
                "You're not really talking about me — are you?")),

        // ── Questions ─────────────────────────────────────────────────────
        K("why", 3,
            Rule(@"why don'?t you (.*)",
                "Do you believe I don't (1)?",
                "Perhaps I will (1) in good time.",
                "Should you (1) yourself?"),
            Rule(@"why can'?t i (.*)",
                "Do you think you should be able to (1)?",
                "Have you any idea why you can't (1)?",
                "Do you want to be able to (1)?"),
            Rule(@"(.*)",
                "Why do you ask?",
                "Does that question interest you?",
                "What is it you really want to know?",
                "Are such questions much on your mind?")),

        K("how", 2,
            Rule(@"(.*)",
                "Why do you ask?",
                "How would an answer to that help you?",
                "What is it you really want to know?")),

        K("what", 2,
            Rule(@"(.*)",
                "Why do you ask?",
                "Does that question interest you?",
                "What would an answer mean to you?")),

        K("can", 2,
            Rule(@"can you (.*)",
                "You believe I can (1), don't you?",
                "Whether or not I can (1) depends on you more than on me.",
                "Why do you ask if I can (1)?"),
            Rule(@"can i (.*)",
                "Whether or not you can (1) depends on you more than on me.",
                "Do you want to be able to (1)?",
                "Perhaps you don't want to (1).")),

        K("are", 2,
            Rule(@"are you (.*)",
                "Why are you interested in whether I am (1) or not?",
                "Would you prefer if I were not (1)?",
                "Do you sometimes think I am (1)?"),
            Rule(@"are (.*)",
                "Did you think they might not be (1)?",
                "Would you like it if they were not (1)?")),

        // ── Affirmations / negations ──────────────────────────────────────
        K("yes", 1,
            Rule(@"(.*)",
                "You seem quite positive.",
                "Are you sure?",
                "I see.",
                "I understand.")),

        K("no", 1,
            Rule(@"no one (.*)",
                "Are you sure no one (1)?",
                "Surely someone (1).",
                "Can you think of anyone at all?"),
            Rule(@"(.*)",
                "Are you being negative just to resist?",
                "You're being a bit negative.",
                "Why not?",
                "Why 'no'?")),

        // ── Hedging / uncertainty ─────────────────────────────────────────
        K("perhaps", 3,
            Rule(@"(.*)",
                "You don't seem quite certain.",
                "Why the uncertain tone?",
                "Can't you be more positive?",
                "You aren't sure?")),

        K("maybe", 3,
            Rule(@"(.*)",
                "You don't seem quite certain.",
                "Why the uncertain tone?",
                "Can't you be more positive?",
                "You aren't sure?")),

        K("always", 2,
            Rule(@"(.*)",
                "Can you think of a specific example?",
                "When?",
                "Really, always?",
                "Can you be more precise?")),

        // ── Similarity / analogy ──────────────────────────────────────────
        K("like", 1,
            Rule(@"(.*) (is|am|are) like (.*)",
                "In what way is (1) like (3)?",
                "What resemblance do you see between (1) and (3)?",
                "What does that similarity suggest to you?",
                "Could there really be some connection between (1) and (3)?",
                "How?")),

        // ── Generalizations ───────────────────────────────────────────────
        K("everyone", 2,
            Rule(@"(.*) (everyone|everybody|nobody|no one) (.*)",
                "Really, (2)?",
                "Surely not (2).",
                "Can you think of anyone in particular?",
                "Who, for example?"),
            Rule(@"(.*)",
                "Really?",
                "Surely not everyone.",
                "Can you be more specific?")),

        // ── Cause / apology ───────────────────────────────────────────────
        K("because", 4,
            Rule(@"(.*)",
                "Is that the real reason?",
                "Don't any other reasons come to mind?",
                "Does that reason explain anything else?",
                "What other reasons might there be?")),

        K("sorry", 2,
            Rule(@"(.*)",
                "Please don't apologize.",
                "Apologies are not necessary.",
                "It did not bother me. Please continue.")),
    ];

    // ── Script DSL ────────────────────────────────────────────────────────

    /// <summary>
    /// Factory helper that constructs a <see cref="Keyword"/> entry for the DOCTOR script.
    /// Exists purely to keep <see cref="BuildScript"/> readable by eliminating the
    /// <c>new Keyword(…)</c> boilerplate.
    /// </summary>
    /// <param name="word">The trigger word or phrase, matched case-insensitively against the normalised input.</param>
    /// <param name="priority">
    /// Numeric priority used to rank this keyword against others found in the same input.
    /// Higher values are processed first.
    /// </param>
    /// <param name="rules">One or more decomposition rules tried in the order provided.</param>
    /// <returns>A new <see cref="Keyword"/> instance.</returns>
    private static Keyword K(string word, int priority, params DecompRule[] rules) =>
        new(word, priority, rules);

    /// <summary>
    /// Factory helper that constructs a <see cref="DecompRule"/> for use inside
    /// <see cref="BuildScript"/>. Compiles <paramref name="pattern"/> once with
    /// <see cref="RegexOptions.IgnoreCase"/> and <see cref="RegexOptions.Compiled"/>.
    /// </summary>
    /// <param name="pattern">
    /// A regular expression matched against the full normalised input.
    /// Capture groups correspond to the <c>(1)</c>, <c>(2)</c>, … placeholders
    /// used in <paramref name="reassemblies"/>.
    /// </param>
    /// <param name="reassemblies">
    /// One or more response templates cycled through on successive matches of this rule.
    /// At least one template must be provided.
    /// </param>
    /// <returns>A new <see cref="DecompRule"/> with a compiled pattern.</returns>
    private static DecompRule Rule(string pattern, params string[] reassemblies) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), reassemblies);
}
