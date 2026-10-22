namespace QtsBilling.Ui.Models;

/// <summary>
/// One write intention: the key it was sent under and the exact body it was sent with, held while the answer is
/// outstanding. A money movement in this product is never typed twice on a lost response - the screen re-sends the
/// identical document under the identical key and lets the server's answer decide whether the intention is released
/// (specification F04). Shared by the payables, receivables and bank-reconciliation workbenches so one discipline
/// cannot drift into three.
/// </summary>
public sealed class HeldIntention<TBody>(string name)
{
    /// <summary>What the operator would call this act, for the sentence that names it back.</summary>
    public string Name { get; } = name;

    public string? Key { get; set; }

    public TBody? Body { get; set; }

    /// <summary>True while the server's answer to this exact document is still unknown.</summary>
    public bool Outstanding => Key is not null;

    public void Release()
    {
        Key = null;
        Body = default;
    }
}
