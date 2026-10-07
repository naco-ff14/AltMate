namespace AltMate;
internal static class LotteryPayment
{
    internal static bool Confirmed(uint before, uint after, uint price) =>
        price > 0 && before >= price && before - price == after;
}
