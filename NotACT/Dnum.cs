namespace Advanced_Combat_Tracker;

public class Dnum : IComparable
{
    private string damageString;

    public Dnum(long NumberValue)
    {
        Number = NumberValue;
        damageString = string.Empty;
    }

    public Dnum(long NumberValue, string CustomDamageString)
    {
        Number = NumberValue;
        damageString = CustomDamageString;
    }

    public static Dnum NoDamage => 0L;
    public static Dnum Miss => -1L;
    public static Dnum Unknown => -9L;
    public static Dnum Death => -10L;
    public static Dnum ThreatPosition => -11L;

    // 只在 NotACT 內部拿來做 ==／!= 比較、永不外流：比較結果與每次新建的 Death／Miss 相同，省掉每一下招式一個物件。
    internal static readonly Dnum DeathComparand = Death;
    internal static readonly Dnum MissComparand = Miss;

    // 與 (long)(a + b)（a 先隱含轉成 Dnum，再走下面的 operator +）相同，但不配置兩個 Dnum；
    // 隱含轉換把小於 -10 的 a 變成 -9，兩者都是負數、走同一個分支，所以不必另外轉。
    internal static long AddAsDnum(long a, long b)
    {
        if (a > -1 && b > -1) return a + b;
        if (a < 0 && b >= 0) return b;
        if (b < 0 && a >= 0) return a;
        return 0L;
    }

    public long Number { get; }

    public string DamageString
    {
        get => string.IsNullOrEmpty(damageString) ? ToString() : damageString;
        set => damageString = value;
    }

    public string DamageString2 { get; set; }

    public int CompareTo(object? obj)
    {
        var otherDnum = (Dnum)obj!;
        var thisNum = Number;
        var otherNum = otherDnum.Number;
        if (thisNum == -9 && otherNum == -9)
        {
            return damageString.CompareTo(otherDnum.damageString);
        }

        return thisNum.CompareTo(otherNum);
    }

    public static implicit operator long(Dnum val) => val.Number;

    public static implicit operator Dnum(long val)
    {
        return val >= -10 ? new Dnum(val) : new Dnum(-9L);
    }

    public static Dnum operator +(Dnum a, Dnum b)
    {
        if (a.Number > -1 && b.Number > -1) return new Dnum(a.Number + b.Number);

        if (a.Number < 0 && b.Number >= 0) return new Dnum(b.Number);

        if (b.Number < 0 && a.Number >= 0) return new Dnum(a.Number);

        return new Dnum(0L);
    }

    public static bool operator ==(Dnum a, Dnum b) => a.Equals(b);

    public static bool operator !=(Dnum a, Dnum b) => !a.Equals(b);

    public override string ToString()
    {
        if (Number > 0 && string.IsNullOrEmpty(damageString))
            return Number.ToString(ActGlobals.mainTableShowCommas ? "#,0" : "0");
        
        var number2 = Number + 10;
        if ((ulong)number2 > 10uL) return damageString + DamageString2;
        return number2 switch
        {
            10L => "data-dnumNoDamage",
            9L => "data-dnumMiss",
            8L => "data-dnumResist",
            7L => "data-dnumParry",
            6L => "data-dnumRiposte",
            5L => "data-dnumBlock",
            0L => "data-dnumDeath",
            _ => damageString + DamageString2
        };
    }

    public string ToString(bool ShortHand)
    {
        if (Number > 0)
            return ShortHand ? Number.ToString() : Number.ToString(ActGlobals.mainTableShowCommas ? "#,0" : "0");

        if (!ShortHand)
        {
            return Number switch
            {
                0L => "data-dnumNoDamage",
                -1L => "data-dnumMiss",
                -10L => "data-dnumDeath",
                _ => damageString
            };
        }

        return Number switch
        {
            0L => "0",
            -1L => "data-dnumMiss",
            -10L => "data-dnumDeath",
            _ => damageString.Length < 3 ? damageString : damageString[..3]
        };
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Dnum dnum)
            return false;
        return dnum.Number == Number && DamageString.Equals(dnum.DamageString);
    }

    public override int GetHashCode() => ToString(false).GetHashCode();
}
