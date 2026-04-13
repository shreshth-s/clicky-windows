using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;

namespace ClickyWindows.Services;

/// <summary>
/// Uses Windows UI Automation to snap a Claude-supplied screen coordinate to
/// the center of a real interactable UI element.
///
/// Inputs and outputs are in physical screen pixels. The caller is responsible
/// for converting DIPs to physical pixels before calling this helper.
/// </summary>
public static class UiAutomationSnapper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    private static readonly ControlType[] InteractableControlTypes =
    {
        ControlType.Button,
        ControlType.Hyperlink,
        ControlType.MenuItem,
        ControlType.ListItem,
        ControlType.TabItem,
        ControlType.TreeItem,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.ComboBox,
        ControlType.SplitButton,
        ControlType.Edit,
    };

    private static readonly HashSet<string> DescriptionStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "and",
        "at",
        "button",
        "click",
        "icon",
        "in",
        "item",
        "of",
        "on",
        "open",
        "please",
        "press",
        "select",
        "tab",
        "taskbar",
        "the",
        "to",
        "toolbar",
        "window",
    };

    // Reject anything larger than this because giant containers are usually not
    // reliable click targets.
    private const double MaxInteractableWidthPhysical = 400;
    private const double MaxInteractableHeightPhysical = 400;
    private const double MinInteractableWidthPhysical = 4;
    private const double MinInteractableHeightPhysical = 4;

    private const int NearbyProbeStepPixels = 16;
    private const int MaxAncestorDepth = 6;
    private const int MaxUiAutomationNodesToInspectPerRoot = 2500;

    public readonly record struct SnapResult(double X, double Y, string ElementName, string ControlType);

    private readonly record struct InteractableCandidate(
        double CenterX,
        double CenterY,
        Rect BoundingRectangle,
        string ElementName,
        string AutomationId,
        string ControlType,
        double DistanceFromRequestedPoint
    );

    /// <summary>
    /// Snaps to an interactable near (centerX, centerY). If targetDescription is
    /// provided, this first tries name-aware matching, then falls back to nearest
    /// proximity snapping.
    /// </summary>
    public static SnapResult? TrySnapNearby(
        double centerX,
        double centerY,
        double searchRadiusPhysical,
        string? targetDescription = null
    )
    {
        var nearbyCandidates = CollectNearbyInteractableCandidates(
            centerX,
            centerY,
            searchRadiusPhysical
        );

        string normalizedTargetDescription = NormalizeForMatching(targetDescription ?? "");
        bool hasMeaningfulDescription = TokenizeForMatching(normalizedTargetDescription).Count > 0;

        if (hasMeaningfulDescription)
        {
            var bestNearbyNameMatch = SelectBestNameMatch(
                nearbyCandidates,
                normalizedTargetDescription,
                distanceNormalizationRadius: searchRadiusPhysical,
                minimumNameScore: 0.50
            );
            if (bestNearbyNameMatch != null)
            {
                return ToSnapResult(bestNearbyNameMatch.Value);
            }

            var bestGlobalNameMatch = TryFindNameMatchInLikelyRoots(
                normalizedTargetDescription,
                centerX,
                centerY
            );
            if (bestGlobalNameMatch != null)
            {
                return ToSnapResult(bestGlobalNameMatch.Value);
            }
        }

        if (nearbyCandidates.Count == 0)
        {
            return null;
        }

        return ToSnapResult(nearbyCandidates[0]);
    }

    private static List<InteractableCandidate> CollectNearbyInteractableCandidates(
        double centerX,
        double centerY,
        double searchRadiusPhysical
    )
    {
        var candidatesByKey = new Dictionary<string, InteractableCandidate>(
            StringComparer.OrdinalIgnoreCase
        );

        int maxRingIndex = (int)Math.Ceiling(searchRadiusPhysical / NearbyProbeStepPixels);
        for (int ringIndex = 0; ringIndex <= maxRingIndex; ringIndex++)
        {
            foreach (var (probeX, probeY) in GenerateSquareRingProbePoints(centerX, centerY, ringIndex))
            {
                var candidate = TryFindCandidateAtProbePoint(
                    probeX,
                    probeY,
                    centerX,
                    centerY
                );
                if (candidate == null)
                {
                    continue;
                }

                string dedupeKey = BuildCandidateDeduplicationKey(candidate.Value);
                if (!candidatesByKey.TryGetValue(dedupeKey, out var existingCandidate))
                {
                    candidatesByKey[dedupeKey] = candidate.Value;
                    continue;
                }

                if (candidate.Value.DistanceFromRequestedPoint < existingCandidate.DistanceFromRequestedPoint)
                {
                    candidatesByKey[dedupeKey] = candidate.Value;
                }
            }
        }

        return candidatesByKey
            .Values
            .OrderBy(candidate => candidate.DistanceFromRequestedPoint)
            .ToList();
    }

    private static IEnumerable<(double ProbeX, double ProbeY)> GenerateSquareRingProbePoints(
        double centerX,
        double centerY,
        int ringIndex
    )
    {
        if (ringIndex == 0)
        {
            yield return (centerX, centerY);
            yield break;
        }

        int ringStep = ringIndex * NearbyProbeStepPixels;

        for (int xStep = -ringIndex; xStep <= ringIndex; xStep++)
        {
            yield return (
                centerX + (xStep * NearbyProbeStepPixels),
                centerY - ringStep
            );
            yield return (
                centerX + (xStep * NearbyProbeStepPixels),
                centerY + ringStep
            );
        }

        for (int yStep = -(ringIndex - 1); yStep <= ringIndex - 1; yStep++)
        {
            yield return (
                centerX - ringStep,
                centerY + (yStep * NearbyProbeStepPixels)
            );
            yield return (
                centerX + ringStep,
                centerY + (yStep * NearbyProbeStepPixels)
            );
        }
    }

    private static InteractableCandidate? TryFindCandidateAtProbePoint(
        double probeX,
        double probeY,
        double requestedCenterX,
        double requestedCenterY
    )
    {
        try
        {
            var hitElement = AutomationElement.FromPoint(new System.Windows.Point(probeX, probeY));
            if (hitElement == null)
            {
                return null;
            }

            var controlViewWalker = TreeWalker.ControlViewWalker;
            var currentElement = hitElement;

            for (int depth = 0; depth < MaxAncestorDepth && currentElement != null; depth++)
            {
                if (IsValidInteractableForProbe(currentElement, probeX, probeY))
                {
                    return CreateCandidateFromElement(
                        currentElement,
                        requestedCenterX,
                        requestedCenterY
                    );
                }

                currentElement = SafeGet(() => controlViewWalker.GetParent(currentElement));
            }
        }
        catch
        {
            // UI Automation can throw if the element vanishes while being queried.
        }

        return null;
    }

    private static InteractableCandidate? TryFindNameMatchInLikelyRoots(
        string normalizedTargetDescription,
        double requestedCenterX,
        double requestedCenterY
    )
    {
        var requestedScreen = Screen.FromPoint(
            new DrawingPoint(
                (int)Math.Round(requestedCenterX),
                (int)Math.Round(requestedCenterY)
            )
        );

        var candidateRoots = GetLikelySearchRoots();
        var bestCandidate = default(InteractableCandidate?);
        double bestCombinedScore = double.NegativeInfinity;

        foreach (var rootElement in candidateRoots)
        {
            foreach (var candidate in EnumerateInteractableCandidates(rootElement, requestedCenterX, requestedCenterY))
            {
                if (!IsCandidateOnSameScreen(candidate, requestedScreen))
                {
                    continue;
                }

                double nameScore = ComputeNameSimilarityScore(
                    normalizedTargetDescription,
                    candidate.ElementName,
                    candidate.AutomationId
                );
                if (nameScore < 0.70)
                {
                    continue;
                }

                // Global name search is a fallback, so name score dominates.
                double distanceScore = 1.0 - Math.Min(1.0, candidate.DistanceFromRequestedPoint / 1500.0);
                double combinedScore = (nameScore * 0.92) + (distanceScore * 0.08);
                if (combinedScore > bestCombinedScore)
                {
                    bestCombinedScore = combinedScore;
                    bestCandidate = candidate;
                }
            }
        }

        return bestCandidate;
    }

    private static IReadOnlyList<AutomationElement> GetLikelySearchRoots()
    {
        var rootsByRuntimeId = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);

        void AddRoot(AutomationElement? root)
        {
            if (root == null)
            {
                return;
            }

            string runtimeIdKey = string.Join(",", SafeGet(() => root.GetRuntimeId()) ?? Array.Empty<int>());
            if (runtimeIdKey.Length == 0)
            {
                runtimeIdKey = Guid.NewGuid().ToString("N");
            }

            rootsByRuntimeId.TryAdd(runtimeIdKey, root);
        }

        AddRoot(TryGetAutomationElementFromHandle(GetForegroundWindow()));
        AddRoot(TryGetAutomationElementFromHandle(FindWindow("Shell_TrayWnd", null)));
        AddRoot(TryGetAutomationElementFromHandle(FindWindow("Shell_SecondaryTrayWnd", null)));

        return rootsByRuntimeId.Values.ToList();
    }

    private static AutomationElement? TryGetAutomationElementFromHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return AutomationElement.FromHandle(windowHandle);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<InteractableCandidate> EnumerateInteractableCandidates(
        AutomationElement rootElement,
        double requestedCenterX,
        double requestedCenterY
    )
    {
        var controlViewWalker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(rootElement);

        int visitedNodeCount = 0;
        while (queue.Count > 0 && visitedNodeCount < MaxUiAutomationNodesToInspectPerRoot)
        {
            var currentElement = queue.Dequeue();
            visitedNodeCount++;

            if (IsInteractable(currentElement) && IsSensibleBounds(currentElement.Current.BoundingRectangle))
            {
                var candidate = CreateCandidateFromElement(
                    currentElement,
                    requestedCenterX,
                    requestedCenterY
                );
                if (candidate != null)
                {
                    yield return candidate.Value;
                }
            }

            var firstChild = SafeGet(() => controlViewWalker.GetFirstChild(currentElement));
            while (firstChild != null)
            {
                queue.Enqueue(firstChild);
                firstChild = SafeGet(() => controlViewWalker.GetNextSibling(firstChild));
            }
        }
    }

    private static InteractableCandidate? SelectBestNameMatch(
        IReadOnlyList<InteractableCandidate> candidates,
        string normalizedTargetDescription,
        double distanceNormalizationRadius,
        double minimumNameScore
    )
    {
        var bestCandidate = default(InteractableCandidate?);
        double bestCombinedScore = double.NegativeInfinity;

        foreach (var candidate in candidates)
        {
            double nameScore = ComputeNameSimilarityScore(
                normalizedTargetDescription,
                candidate.ElementName,
                candidate.AutomationId
            );
            if (nameScore < minimumNameScore)
            {
                continue;
            }

            double distanceScore = 1.0 - Math.Min(
                1.0,
                candidate.DistanceFromRequestedPoint / Math.Max(distanceNormalizationRadius, 1.0)
            );
            double combinedScore = (nameScore * 0.85) + (distanceScore * 0.15);
            if (combinedScore > bestCombinedScore)
            {
                bestCombinedScore = combinedScore;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    private static bool IsCandidateOnSameScreen(InteractableCandidate candidate, Screen requestedScreen)
    {
        var candidatePoint = new DrawingPoint(
            (int)Math.Round(candidate.CenterX),
            (int)Math.Round(candidate.CenterY)
        );
        var candidateScreen = Screen.FromPoint(candidatePoint);

        return string.Equals(
            candidateScreen.DeviceName,
            requestedScreen.DeviceName,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static double ComputeNameSimilarityScore(
        string normalizedTargetDescription,
        string candidateName,
        string candidateAutomationId
    )
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetDescription))
        {
            return 0;
        }

        string normalizedCandidateText = NormalizeForMatching(
            $"{candidateName} {candidateAutomationId}"
        );
        if (normalizedCandidateText.Length == 0)
        {
            return 0;
        }

        if (normalizedCandidateText.Contains(normalizedTargetDescription, StringComparison.Ordinal))
        {
            return 1.0;
        }

        var descriptionTokens = TokenizeForMatching(normalizedTargetDescription);
        if (descriptionTokens.Count == 0)
        {
            return 0;
        }

        var candidateTokens = TokenizeForMatching(normalizedCandidateText);
        if (candidateTokens.Count == 0)
        {
            return 0;
        }

        var candidateTokenSet = new HashSet<string>(candidateTokens, StringComparer.OrdinalIgnoreCase);
        int matchedTokenCount = descriptionTokens.Count(candidateTokenSet.Contains);
        if (matchedTokenCount == 0)
        {
            return 0;
        }

        double coverageScore = matchedTokenCount / (double)descriptionTokens.Count;

        bool hasConsecutivePhraseMatch = HasConsecutiveTokenPhraseMatch(
            descriptionTokens,
            normalizedCandidateText
        );
        double phraseBonus = hasConsecutivePhraseMatch ? 0.15 : 0.0;

        return Math.Min(1.0, coverageScore + phraseBonus);
    }

    private static bool HasConsecutiveTokenPhraseMatch(
        IReadOnlyList<string> descriptionTokens,
        string normalizedCandidateText
    )
    {
        if (descriptionTokens.Count < 2)
        {
            return false;
        }

        for (int index = 0; index < descriptionTokens.Count - 1; index++)
        {
            string phrase = $"{descriptionTokens[index]} {descriptionTokens[index + 1]}";
            if (normalizedCandidateText.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> TokenizeForMatching(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return new List<string>();
        }

        return normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token =>
                token.Length >= 2 &&
                !DescriptionStopWords.Contains(token) &&
                !token.All(char.IsDigit)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[text.Length];
        int outputIndex = 0;

        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[outputIndex++] = char.ToLowerInvariant(character);
            }
            else
            {
                buffer[outputIndex++] = ' ';
            }
        }

        string cleaned = new string(buffer[..outputIndex]);
        return string.Join(
            " ",
            cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        );
    }

    private static InteractableCandidate? CreateCandidateFromElement(
        AutomationElement element,
        double requestedCenterX,
        double requestedCenterY
    )
    {
        var boundingRectangle = element.Current.BoundingRectangle;
        if (!IsSensibleBounds(boundingRectangle))
        {
            return null;
        }

        double centerX = boundingRectangle.X + (boundingRectangle.Width / 2.0);
        double centerY = boundingRectangle.Y + (boundingRectangle.Height / 2.0);

        string elementName = SafeGet(() => element.Current.Name ?? "") ?? "";
        string automationId = SafeGet(() => element.Current.AutomationId ?? "") ?? "";
        string controlTypeName = SafeGet(() => element.Current.ControlType?.ProgrammaticName ?? "") ?? "";

        if (string.IsNullOrWhiteSpace(elementName))
        {
            elementName = automationId;
        }

        double distanceFromRequestedPoint = Distance(
            centerX,
            centerY,
            requestedCenterX,
            requestedCenterY
        );

        return new InteractableCandidate(
            centerX,
            centerY,
            boundingRectangle,
            elementName,
            automationId,
            controlTypeName,
            distanceFromRequestedPoint
        );
    }

    private static bool IsValidInteractableForProbe(
        AutomationElement element,
        double probeX,
        double probeY
    )
    {
        if (!IsInteractable(element))
        {
            return false;
        }

        var boundingRectangle = element.Current.BoundingRectangle;
        return IsSensibleBounds(boundingRectangle) && boundingRectangle.Contains(probeX, probeY);
    }

    private static bool IsSensibleBounds(Rect boundingRectangle)
    {
        return
            !boundingRectangle.IsEmpty &&
            boundingRectangle.Width >= MinInteractableWidthPhysical &&
            boundingRectangle.Height >= MinInteractableHeightPhysical &&
            boundingRectangle.Width <= MaxInteractableWidthPhysical &&
            boundingRectangle.Height <= MaxInteractableHeightPhysical;
    }

    private static bool IsInteractable(AutomationElement element)
    {
        try
        {
            var controlType = element.Current.ControlType;
            if (controlType == null)
            {
                return false;
            }

            if (InteractableControlTypes.Contains(controlType))
            {
                return true;
            }

            if (SafeGetBoolean(() => element.TryGetCurrentPattern(InvokePattern.Pattern, out _)))
            {
                return true;
            }

            if (SafeGetBoolean(() => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static SnapResult ToSnapResult(InteractableCandidate candidate)
    {
        string displayName = candidate.ElementName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "(unnamed)";
        }

        return new SnapResult(
            candidate.CenterX,
            candidate.CenterY,
            displayName,
            candidate.ControlType
        );
    }

    private static string BuildCandidateDeduplicationKey(InteractableCandidate candidate)
    {
        return string.Join(
            "|",
            candidate.BoundingRectangle.X.ToString("F1"),
            candidate.BoundingRectangle.Y.ToString("F1"),
            candidate.BoundingRectangle.Width.ToString("F1"),
            candidate.BoundingRectangle.Height.ToString("F1"),
            NormalizeForMatching(candidate.ElementName),
            NormalizeForMatching(candidate.AutomationId),
            candidate.ControlType
        );
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double deltaX = x1 - x2;
        double deltaY = y1 - y2;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static T? SafeGet<T>(Func<T> getter) where T : class
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeGetBoolean(Func<bool> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return false;
        }
    }
}
