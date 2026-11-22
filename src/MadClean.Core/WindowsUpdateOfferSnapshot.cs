namespace MadClean.Core;

/// <summary>Accept one provider response for all Update Center offer views.</summary>
public static class WindowsUpdateOfferSnapshot
{
    public static CheckResult<SystemUpdate> Accept(CheckResult<SystemUpdate>? response)
    {
        if (response == null || !Enum.IsDefined(response.State) || response.Items == null)
            return InvalidResponse();

        if (response.State is not (CheckState.Available or CheckState.Partial))
        {
            return response.Items.Count == 0
                ? response with { Items = Array.Empty<SystemUpdate>() }
                : CheckResult<SystemUpdate>.Empty(response.State,
                    "The provider returned offers alongside an unusable result. The offers were discarded. Retry the Windows Update check.");
        }

        var offers = response.Items.ToArray();
        if (offers.Any(offer => offer == null || offer.Kind is not ("Software" or "Driver" or "Firmware" or "Unknown")))
            return InvalidResponse();

        // Freeze membership while retaining each exact provider row for selected-offer review.
        return response with { Items = Array.AsReadOnly(offers) };
    }

    private static CheckResult<SystemUpdate> InvalidResponse() =>
        CheckResult<SystemUpdate>.Empty(CheckState.Unknown,
            "Windows Update returned an inconsistent response. No offers were accepted. Retry the check.");
}
