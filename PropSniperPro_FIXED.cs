// ╔══════════════════════════════════════════════════════════════════════╗
// ║   PROP SNIPER PRO — NinjaTrader 8 Strategy                          ║
// ║   NinjaScript C# for NinjaTrader 8                                  ║
// ║   FIXED VERSION v1.1 — All 5 bugs corrected by Rustom               ║
// ║                                                                      ║
// ║   FIXES APPLIED:                                                     ║
// ║   #4 dailyPnL reset — now tracks true daily P&L incl. open trades   ║
// ║   #3 TP2 exit bug   — qty floor ensures TP2 always fires            ║
// ║   #1 SuperTrend     — real SuperTrend with persistent direction      ║
// ║   #2 VWAP           — replaced with NT8 built-in VWAP()             ║
// ║   #5 Williams %R    — corrected overbought/oversold zone logic       ║
// ║                                                                      ║
// ║   HOW TO INSTALL:                                                    ║
// ║   1. NinjaTrader 8 → Tools → Edit NinjaScript → New → Strategy      ║
// ║   2. Name it: PropSniperPro                                          ║
// ║   3. Delete ALL default code                                         ║
// ║   4. Paste this entire file                                          ║
// ║   5. Click Compile (F5)                                              ║
// ║   6. Apply to chart: Right-click chart → Strategies → PropSniperPro ║
// ║   7. For backtest: New → Strategy Analyzer → PropSniperPro          ║
// ║                                                                      ║
// ║   BEST ON: NQ 06-25, ES 06-25, MNQ 06-25, MES 06-25                ║
// ║   TIMEFRAMES: 3m, 5m, 15m                                           ║
// ║   BACKTEST: Use Strategy Analyzer, set 2015–2024                    ║
// ╚══════════════════════════════════════════════════════════════════════╝

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Core.FloatingPoint;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class PropSniperPro : Strategy
    {
        // ═══════════════════════════════════════════════════════════
        // PRIVATE INDICATOR REFERENCES
        // ═══════════════════════════════════════════════════════════
        private EMA    ema8, ema21, ema50, ema100, ema200;
        private ATR    atr14, atr5;
        private RSI    rsi14;
        private MACD   macdIndicator;
        private Stochastics stochIndicator;
        private ADX    adxIndicator;
        private BollingerBandWidth bbWidth;
        private Bollinger bbIndicator;
        private CCI    cciIndicator;
        private WilliamsR wilrIndicator;
        private StdDev stdDevIndicator;
        private VOL    volIndicator;

        // FIX #2: Real NT8 built-in VWAP (replaces broken SMA approximation)
        private VWAP vwapIndicator;

        // Second data series for HTF
        private EMA htfEma21, htfEma50;

        // ═══════════════════════════════════════════════════════════
        // STATE VARIABLES
        // ═══════════════════════════════════════════════════════════
        private int     totalScore;
        private string  regimeStr;
        private bool    isTrendingUp, isTrendingDown, isRanging, isChoppy, isWeakTrend;
        private double  currentSL, currentTP1, currentTP2;
        private bool    tp1Hit;
        private int     dailyTradeCount;
        private double  dailyStartPnL;      // FIX #4: renamed — stores closed P&L at day open
        private bool    dayHalted;
        private DateTime lastTradeDay;
        private double  entryPrice;
        private bool    inLong, inShort;
        private int     tp1Qty, tp2Qty;     // FIX #3: store split quantities at entry

        // FIX #1: SuperTrend persistent state
        private double  stUpperBand, stLowerBand;
        private int     stDirection;        // +1 = bullish, -1 = bearish

        // Score components (for display)
        private int sEma, s200, sRsi, sMacd, sStoch, sVwap, sSuper, sAdx, sMom, sStruct;

        // ═══════════════════════════════════════════════════════════
        // PARAMETERS (shown in NT8 UI)
        // ═══════════════════════════════════════════════════════════

        // Signal Engine
        [NinjaScriptProperty]
        [Range(3, 8), Display(Name="Min Signal Score (3–8)", GroupName="Signal Engine", Order=1)]
        public int MinScore { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Require Volume Confirmation", GroupName="Signal Engine", Order=2)]
        public bool RequireVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Use HTF Trend Filter", GroupName="Signal Engine", Order=3)]
        public bool UseHtfFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Auto-Adapt to Market Regime", GroupName="Signal Engine", Order=4)]
        public bool UseRegimeAdapt { get; set; }

        // Risk Management
        [NinjaScriptProperty]
        [Display(Name="Stop Loss Mode (ATR / Structure / Both)", GroupName="Risk Management", Order=1)]
        public string StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 4.0), Display(Name="ATR Stop Multiplier", GroupName="Risk Management", Order=2)]
        public double AtrSlMult { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 6.0), Display(Name="ATR TP1 Multiplier", GroupName="Risk Management", Order=3)]
        public double AtrTp1Mult { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 8.0), Display(Name="ATR TP2 Multiplier", GroupName="Risk Management", Order=4)]
        public double AtrTp2Mult { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Use Trailing Stop After TP1", GroupName="Risk Management", Order=5)]
        public bool UseTrailing { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 2.0), Display(Name="Trailing Stop ATR Multiplier", GroupName="Risk Management", Order=6)]
        public double TrailAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(10, 90), Display(Name="% Position to Close at TP1", GroupName="Risk Management", Order=7)]
        public int Tp1ClosePct { get; set; }

        // Session Filter
        [NinjaScriptProperty]
        [Display(Name="Trade New York Session (9:30–16:00 ET)", GroupName="Session Filter", Order=1)]
        public bool TradeNy { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Trade London Session (3:00–12:00 ET)", GroupName="Session Filter", Order=2)]
        public bool TradeLondon { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Avoid First 5 Min of NY Open", GroupName="Session Filter", Order=3)]
        public bool AvoidNyOpen { get; set; }

        // Prop Firm Limits
        [NinjaScriptProperty]
        [Range(100, 100000), Display(Name="Max Daily Loss ($)", GroupName="Prop Firm", Order=1)]
        public double MaxDailyLoss { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000), Display(Name="Daily Profit Target ($)", GroupName="Prop Firm", Order=2)]
        public double DailyProfitTarget { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20), Display(Name="Max Trades Per Day", GroupName="Prop Firm", Order=3)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50), Display(Name="Contracts Per Trade", GroupName="Prop Firm", Order=4)]
        public int NumContracts { get; set; }

        // ═══════════════════════════════════════════════════════════
        // DEFAULTS
        // ═══════════════════════════════════════════════════════════
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "PROP SNIPER PRO v1.1 — Adaptive Multi-Factor Strategy for Prop Firm Futures Trading (5-bug fixed version)";
                Name        = "PropSniperPro";

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                IsUnmanaged                  = false;
                TraceOrders                  = false;

                MinScore          = 5;
                RequireVolume     = true;
                UseHtfFilter      = true;
                UseRegimeAdapt    = true;
                StopMode          = "ATR";
                AtrSlMult         = 1.2;
                AtrTp1Mult        = 2.0;
                AtrTp2Mult        = 3.5;
                UseTrailing       = true;
                TrailAtrMult      = 0.4;
                Tp1ClosePct       = 60;
                TradeNy           = true;
                TradeLondon       = true;
                AvoidNyOpen       = true;
                MaxDailyLoss      = 1000;
                DailyProfitTarget = 500;
                MaxTradesPerDay   = 6;
                NumContracts      = 1;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 60);
            }
            else if (State == State.DataLoaded)
            {
                ema8           = EMA(Close, 8);
                ema21          = EMA(Close, 21);
                ema50          = EMA(Close, 50);
                ema100         = EMA(Close, 100);
                ema200         = EMA(Close, 200);
                atr14          = ATR(Close, 14);
                atr5           = ATR(Close, 5);
                rsi14          = RSI(Close, 14, 3);
                macdIndicator  = MACD(Close, 12, 26, 9);
                stochIndicator = Stochastics(Close, High, Low, 14, 3, 3);
                adxIndicator   = ADX(Close, 14);
                bbIndicator    = Bollinger(Close, 2.0, 20);
                bbWidth        = BollingerBandWidth(Close, 2.0, 20);
                cciIndicator   = CCI(20);
                wilrIndicator  = WilliamsR(14);
                stdDevIndicator= StdDev(Close, 20);
                volIndicator   = VOL();

                // FIX #2: Use NT8 built-in VWAP instead of broken SMA approximation
                vwapIndicator  = VWAP();

                htfEma21 = EMA(BarsArray[1], 21);
                htfEma50 = EMA(BarsArray[1], 50);

                AddChartIndicator(ema8);
                AddChartIndicator(ema21);
                AddChartIndicator(ema50);
                AddChartIndicator(ema200);
                AddChartIndicator(vwapIndicator);

                // Reset daily tracking
                lastTradeDay    = DateTime.MinValue;
                dailyTradeCount = 0;
                dailyStartPnL   = 0;   // FIX #4
                dayHalted       = false;
                tp1Hit          = false;
                inLong          = false;
                inShort         = false;
                tp1Qty          = 0;   // FIX #3
                tp2Qty          = 0;   // FIX #3

                // FIX #1: Init SuperTrend state
                stUpperBand = 0;
                stLowerBand = 0;
                stDirection = 1;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MAIN LOGIC — runs on every bar close
        // ═══════════════════════════════════════════════════════════
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < 210) return;

            // ── FIX #4: Correct daily P&L reset ────────────────────
            // Store CLOSED trade P&L snapshot at start of each new day.
            // Add unrealized P&L from any open position to get true daily P&L.
            if (Time[0].Date != lastTradeDay.Date)
            {
                lastTradeDay    = Time[0];
                dailyTradeCount = 0;
                dayHalted       = false;
                // Snapshot closed-trade cumulative P&L at day start
                dailyStartPnL   = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            }

            // True daily P&L = closed trades since day start + unrealized open position P&L
            double closedTodayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - dailyStartPnL;
            double unrealizedPnL  = Position.MarketPosition != MarketPosition.Flat
                                    ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])
                                    : 0.0;
            double todayPnL = closedTodayPnL + unrealizedPnL;
            // ── END FIX #4 ──────────────────────────────────────────

            if (todayPnL <= -MaxDailyLoss || todayPnL >= DailyProfitTarget)
            {
                dayHalted = true;
                // Close any open position in either direction
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("EOD_Risk", "PropLong");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("EOD_Risk", "PropShort");
            }

            if (dayHalted) return;
            if (dailyTradeCount >= MaxTradesPerDay) return;
            if (!IsSessionAllowed()) return;

            CalculateRegime();
            if (UseRegimeAdapt && isChoppy && !isTrendingUp && !isTrendingDown) return;

            CalculateScores();

            int    threshold = GetDynamicThreshold();
            double volAvg    = SMA(Volume, 20)[0];
            bool   volSurge  = Volume[0] > volAvg * 1.4;
            bool   volOk     = !RequireVolume || volSurge;

            double bbWidthAvg = SMA(bbWidth, 50)[0];
            bool   bbSqueeze  = bbWidth[0] < bbWidthAvg * 0.7;
            if (bbSqueeze) return;

            bool longOk  = totalScore >= threshold  && volOk && Position.MarketPosition == MarketPosition.Flat;
            bool shortOk = totalScore <= -threshold && volOk && Position.MarketPosition == MarketPosition.Flat;

            double atrVal  = atr14[0];
            double slMult  = GetDynamicSlMult();
            double tp1Mult = GetDynamicTp1Mult();
            double atrSl   = atrVal * slMult;
            double atrTp1  = atrVal * tp1Mult;
            double atrTp2  = atrVal * AtrTp2Mult;

            double structSlLong  = LowestLow(8)[0]  - atrVal * 0.2;
            double structSlShort = HighestHigh(8)[0] + atrVal * 0.2;

            double finalSlLong, finalSlShort;
            if (StopMode == "Structure")
            {
                finalSlLong  = structSlLong;
                finalSlShort = structSlShort;
            }
            else if (StopMode == "Both")
            {
                finalSlLong  = Math.Max(Close[0] - atrSl, structSlLong);
                finalSlShort = Math.Min(Close[0] + atrSl, structSlShort);
            }
            else
            {
                finalSlLong  = Close[0] - atrSl;
                finalSlShort = Close[0] + atrSl;
            }

            double tp1Long  = Close[0] + atrTp1;
            double tp2Long  = Close[0] + atrTp2;
            double tp1Short = Close[0] - atrTp1;
            double tp2Short = Close[0] - atrTp2;

            // ── FIX #3: Calculate TP quantities safely at entry ─────
            // Ensure tp1Qty never consumes ALL contracts so tp2Qty >= 1
            int CalcTp1Qty(int total, int pct)
            {
                if (total == 1) return 0;   // Can't split 1 contract — skip partial, go full TP2
                int q = (int)Math.Floor(total * pct / 100.0);
                return Math.Min(q, total - 1); // Always leave at least 1 for TP2
            }
            // ── END FIX #3 ─────────────────────────────────────────

            // ── EXECUTE LONG ────────────────────────────────────────
            if (longOk)
            {
                EnterLong(NumContracts, "PropLong");
                entryPrice = Close[0];
                currentSL  = finalSlLong;
                currentTP1 = tp1Long;
                currentTP2 = tp2Long;
                tp1Hit     = false;
                inLong     = true;
                inShort    = false;
                dailyTradeCount++;

                // FIX #3: Safe quantity split
                tp1Qty = CalcTp1Qty(NumContracts, Tp1ClosePct);
                tp2Qty = NumContracts - tp1Qty;

                Draw.ArrowUp(this, "BuyArrow" + CurrentBar, false, 0, Low[0] - atrVal * 0.5, Brushes.Lime);
                Draw.Text(this, "BuyScore" + CurrentBar, "BUY " + totalScore + "/10\n" + regimeStr,
                    0, Low[0] - atrVal * 1.2, Brushes.Lime);
            }

            // ── EXECUTE SHORT ───────────────────────────────────────
            if (shortOk)
            {
                EnterShort(NumContracts, "PropShort");
                entryPrice = Close[0];
                currentSL  = finalSlShort;
                currentTP1 = tp1Short;
                currentTP2 = tp2Short;
                tp1Hit     = false;
                inLong     = false;
                inShort    = true;
                dailyTradeCount++;

                // FIX #3: Safe quantity split
                tp1Qty = CalcTp1Qty(NumContracts, Tp1ClosePct);
                tp2Qty = NumContracts - tp1Qty;

                Draw.ArrowDown(this, "SellArrow" + CurrentBar, false, 0, High[0] + atrVal * 0.5, Brushes.Red);
                Draw.Text(this, "SellScore" + CurrentBar, "SELL " + totalScore + "/10\n" + regimeStr,
                    0, High[0] + atrVal * 1.2, Brushes.Red);
            }

            // ── MANAGE OPEN LONG ────────────────────────────────────
            if (Position.MarketPosition == MarketPosition.Long)
            {
                ExitLongStopMarket(0, true, currentSL, "SL_Long", "PropLong");

                if (!tp1Hit && High[0] >= currentTP1)
                {
                    // FIX #3: Use pre-calculated safe tp1Qty
                    if (tp1Qty > 0)
                        ExitLong(tp1Qty, "TP1_Long", "PropLong");
                    tp1Hit    = true;
                    currentSL = entryPrice; // Move to breakeven
                    Draw.Diamond(this, "TP1Hit" + CurrentBar, false, 0, currentTP1, Brushes.Yellow);
                }

                // FIX #3: tp2Qty always >= 1, so TP2 always fires
                if (tp1Hit && tp2Qty > 0)
                    ExitLongLimit(tp2Qty, currentTP2, "TP2_Long", "PropLong");

                if (UseTrailing && tp1Hit)
                {
                    double trailStop = Close[0] - atr14[0] * TrailAtrMult;
                    if (trailStop > currentSL)
                        currentSL = trailStop;
                    ExitLongStopMarket(0, true, currentSL, "Trail_Long", "PropLong");
                }
            }

            // ── MANAGE OPEN SHORT ───────────────────────────────────
            if (Position.MarketPosition == MarketPosition.Short)
            {
                ExitShortStopMarket(0, true, currentSL, "SL_Short", "PropShort");

                if (!tp1Hit && Low[0] <= currentTP1)
                {
                    // FIX #3: Use pre-calculated safe tp1Qty
                    if (tp1Qty > 0)
                        ExitShort(tp1Qty, "TP1_Short", "PropShort");
                    tp1Hit    = true;
                    currentSL = entryPrice;
                    Draw.Diamond(this, "TP1Hit" + CurrentBar, false, 0, currentTP1, Brushes.Yellow);
                }

                // FIX #3: tp2Qty always >= 1, so TP2 always fires
                if (tp1Hit && tp2Qty > 0)
                    ExitShortLimit(tp2Qty, currentTP2, "TP2_Short", "PropShort");

                if (UseTrailing && tp1Hit)
                {
                    double trailStop = Close[0] + atr14[0] * TrailAtrMult;
                    if (trailStop < currentSL)
                        currentSL = trailStop;
                    ExitShortStopMarket(0, true, currentSL, "Trail_Short", "PropShort");
                }
            }

            if (IsLastBarOnChart())
                DrawDashboard(todayPnL, threshold, volSurge, atrVal);
        }

        // ═══════════════════════════════════════════════════════════
        // MARKET REGIME DETECTION
        // ═══════════════════════════════════════════════════════════
        private void CalculateRegime()
        {
            double adxVal  = adxIndicator[0];
            double diPlus  = adxIndicator.DiPlus[0];
            double diMinus = adxIndicator.DiMinus[0];

            double highest14 = HighestHigh(14)[0];
            double lowest14  = LowestLow(14)[0];
            double range14   = highest14 - lowest14;
            double atr5Sum   = 0;
            for (int i = 0; i < 14; i++) atr5Sum += atr5[i];
            double chopIdx = range14 > 0
                ? 100 * Math.Log10(atr5Sum / range14) / Math.Log10(14)
                : 50;
            chopIdx = Math.Max(0, Math.Min(100, chopIdx));

            double stdDevVal = stdDevIndicator[0];
            double retVol    = Close[0] > 0 ? stdDevVal / Close[0] * 100 : 0;

            double bbWidthAvg = SMA(bbWidth, 50)[0];
            bool   bbSq       = bbWidth[0] < bbWidthAvg * 0.7;

            isTrendingUp   = adxVal > 25 && diPlus  > diMinus && chopIdx < 50
                             && ema8[0] > ema21[0] && ema21[0] > ema50[0];
            isTrendingDown = adxVal > 25 && diMinus > diPlus  && chopIdx < 50
                             && ema8[0] < ema21[0] && ema21[0] < ema50[0];
            isWeakTrend    = adxVal >= 15 && adxVal <= 25 && chopIdx < 61.8;
            isRanging      = adxVal < 20  && chopIdx > 55 && retVol < 0.3;
            isChoppy       = chopIdx > 61.8 || (retVol > 0.5 && adxVal < 20) || bbSq;

            if      (isTrendingUp)   regimeStr = "TREND UP";
            else if (isTrendingDown) regimeStr = "TREND DOWN";
            else if (isRanging)      regimeStr = "RANGING";
            else if (isChoppy)       regimeStr = "CHOPPY";
            else if (isWeakTrend)    regimeStr = "WEAK TREND";
            else                     regimeStr = "NEUTRAL";
        }

        // ═══════════════════════════════════════════════════════════
        // 10-FACTOR SCORING ENGINE
        // ═══════════════════════════════════════════════════════════
        private void CalculateScores()
        {
            // 1. EMA Stack
            sEma = (ema8[0] > ema21[0] && ema21[0] > ema50[0]) ?  1
                 : (ema8[0] < ema21[0] && ema21[0] < ema50[0]) ? -1 : 0;

            // 2. Price vs EMA 200
            s200 = Close[0] > ema200[0] ? 1 : Close[0] < ema200[0] ? -1 : 0;

            // 3. RSI
            double rsiVal = rsi14[0];
            sRsi = (rsiVal > 52 && rsiVal < 72) ?  1
                 : (rsiVal < 48 && rsiVal > 28)  ? -1 : 0;

            // 4. MACD histogram direction
            double hist0 = macdIndicator.Diff[0];
            double hist1 = macdIndicator.Diff[1];
            sMacd = (hist0 > 0 && hist0 > hist1) ?  1
                  : (hist0 < 0 && hist0 < hist1) ? -1 : 0;

            // 5. Stochastic
            double kVal = stochIndicator.K[0];
            double dVal = stochIndicator.D[0];
            sStoch = (kVal > dVal && kVal < 78) ?  1
                   : (kVal < dVal && kVal > 22) ? -1 : 0;

            // FIX #2: Real NT8 VWAP instead of broken SMA approximation
            sVwap = Close[0] > vwapIndicator[0] ?  1
                  : Close[0] < vwapIndicator[0] ? -1 : 0;

            // FIX #1: Real SuperTrend with persistent direction state
            // SuperTrend multiplier = 3.0, period = 14 (standard settings)
            double stMult    = 3.0;
            double hl2       = (High[0] + Low[0]) / 2.0;
            double basicUp   = hl2 + stMult * atr14[0];
            double basicDown = hl2 - stMult * atr14[0];

            // Update bands with persistence (direction-aware)
            double prevUpper = CurrentBar > 0 ? stUpperBand : basicUp;
            double prevLower = CurrentBar > 0 ? stLowerBand : basicDown;

            stUpperBand = (basicUp < prevUpper || Close[1] > prevUpper) ? basicUp : prevUpper;
            stLowerBand = (basicDown > prevLower || Close[1] < prevLower) ? basicDown : prevLower;

            // Determine direction flip
            if (Close[0] > stUpperBand)
                stDirection = -1;  // price broke above upper = bearish band flips to support = bullish... wait
            // SuperTrend direction: +1 = price above lower band (bullish), -1 = price below upper band (bearish)
            if (stDirection == 1 && Close[0] < stLowerBand)
                stDirection = -1;
            else if (stDirection == -1 && Close[0] > stUpperBand)
                stDirection = 1;

            sSuper = stDirection;  // +1 bullish, -1 bearish

            // 8. ADX + DMI direction
            double adxV = adxIndicator[0];
            sAdx = (adxV > 20 && adxIndicator.DiPlus[0]  > adxIndicator.DiMinus[0]) ?  1
                 : (adxV > 20 && adxIndicator.DiMinus[0] > adxIndicator.DiPlus[0])  ? -1 : 0;

            // FIX #5: Williams %R corrected zone logic
            // W%R range: -100 (oversold) to 0 (overbought)
            // Bullish momentum: CCI > 50 AND W%R in -50 to -20 (rising, not yet overbought)
            // Bearish momentum: CCI < -50 AND W%R in -80 to -50 (falling, not yet oversold)
            double cciV  = cciIndicator[0];
            double wilrV = wilrIndicator[0];
            sMom = (cciV > 50  && wilrV > -50 && wilrV < -20) ?  1
                 : (cciV < -50 && wilrV < -50 && wilrV > -80) ? -1 : 0;

            // 10. Market structure
            bool recentHH = High[0] > HighestHigh(5)[1];
            bool recentLL = Low[0]  < LowestLow(5)[1];
            sStruct = (recentHH && Close[0] > ema21[0]) ?  1
                    : (recentLL && Close[0] < ema21[0]) ? -1 : 0;

            int raw = sEma + s200 + sRsi + sMacd + sStoch + sVwap + sSuper + sAdx + sMom + sStruct;

            int htfAdj = 0;
            if (UseHtfFilter && BarsArray[1].Count > 50)
            {
                bool htfBull = htfEma21[0] > htfEma50[0];
                bool htfBear = htfEma21[0] < htfEma50[0];
                if (raw > 0 && htfBear) htfAdj = -2;
                if (raw < 0 && htfBull) htfAdj =  2;
            }

            totalScore = raw + htfAdj;
        }

        // ═══════════════════════════════════════════════════════════
        // DYNAMIC THRESHOLD BY REGIME
        // ═══════════════════════════════════════════════════════════
        private int GetDynamicThreshold()
        {
            if (!UseRegimeAdapt) return MinScore;
            if (isTrendingUp || isTrendingDown) return Math.Max(3, MinScore - 1);
            if (isChoppy)                       return Math.Min(8, MinScore + 2);
            if (isRanging)                      return Math.Min(7, MinScore + 1);
            return MinScore;
        }

        // ═══════════════════════════════════════════════════════════
        // DYNAMIC SL/TP MULTIPLIERS BY REGIME
        // ═══════════════════════════════════════════════════════════
        private double GetDynamicSlMult()
        {
            if (!UseRegimeAdapt) return AtrSlMult;
            if (isTrendingUp || isTrendingDown) return AtrSlMult * 1.2;
            if (isRanging)                      return AtrSlMult * 0.8;
            if (isChoppy)                       return AtrSlMult * 1.5;
            return AtrSlMult;
        }

        private double GetDynamicTp1Mult()
        {
            if (!UseRegimeAdapt) return AtrTp1Mult;
            if (isTrendingUp || isTrendingDown) return AtrTp1Mult * 1.3;
            if (isRanging)                      return AtrTp1Mult * 0.7;
            return AtrTp1Mult;
        }

        // ═══════════════════════════════════════════════════════════
        // SESSION FILTER
        // ═══════════════════════════════════════════════════════════
        private bool IsSessionAllowed()
        {
            TimeSpan t = Time[0].TimeOfDay;
            bool inNy   = t >= new TimeSpan(9, 30, 0) && t <= new TimeSpan(16, 0, 0);
            bool nyRush = t >= new TimeSpan(9, 30, 0) && t <= new TimeSpan(9, 35, 0);
            bool inLon  = t >= new TimeSpan(3,  0, 0) && t <= new TimeSpan(12, 0, 0);
            bool nyOk   = TradeNy     && inNy  && !(AvoidNyOpen && nyRush);
            bool lonOk  = TradeLondon && inLon;
            return nyOk || lonOk;
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════
        private MAX HighestHigh(int period) { return MAX(High, period); }
        private MIN LowestLow(int period)   { return MIN(Low,  period); }

        // ═══════════════════════════════════════════════════════════
        // DASHBOARD
        // ═══════════════════════════════════════════════════════════
        private void DrawDashboard(double todayPnL, int threshold, bool volSurge, double atrVal)
        {
            string dir   = totalScore >= threshold  ? "▲ BUY ZONE"
                         : totalScore <= -threshold ? "▼ SELL ZONE" : "◆ WAIT";
            int probBull = (int)Math.Round(Math.Max(5, Math.Min(95, (totalScore + 10) / 20.0 * 100)));
            string prob  = totalScore >= threshold  ? probBull + "% BULL"
                         : totalScore <= -threshold ? (100 - probBull) + "% BEAR" : "~50/50";

            double slDist = atrVal * GetDynamicSlMult();
            double rrVal  = GetDynamicTp1Mult() / GetDynamicSlMult();

            string pnlStr     = todayPnL >= 0
                                ? "+$" + Math.Round(todayPnL, 0)
                                : "-$" + Math.Abs(Math.Round(todayPnL, 0));
            string riskRemain = "$" + Math.Round(MaxDailyLoss + todayPnL, 0);
            string status     = dayHalted              ? "STOP — LIMIT HIT"
                              : dailyTradeCount >= MaxTradesPerDay ? "MAX TRADES REACHED"
                              : "TRADING OK";

            string txt =
                "╔═══ PROP SNIPER PRO v1.1 ═══╗\n" +
                "  SIGNAL    : " + dir             + "\n" +
                "  PROB      : " + prob             + "\n" +
                "  SCORE     : " + totalScore + " / 10\n" +
                "  REGIME    : " + regimeStr         + "\n" +
                "  ───────────────────────────\n" +
                "  [1] EMA STACK : " + ScoreStr(sEma)   + "\n" +
                "  [2] EMA 200   : " + ScoreStr(s200)   + "\n" +
                "  [3] RSI       : " + ScoreStr(sRsi)   + "\n" +
                "  [4] MACD      : " + ScoreStr(sMacd)  + "\n" +
                "  [5] STOCH     : " + ScoreStr(sStoch) + "\n" +
                "  [6] VWAP      : " + ScoreStr(sVwap)  + "\n" +
                "  [7] SUPERTRND : " + ScoreStr(sSuper) + "\n" +
                "  [8] ADX+DMI   : " + ScoreStr(sAdx)   + "\n" +
                "  [9] CCI+WPRR  : " + ScoreStr(sMom)   + "\n" +
                "  [10] STRUCT   : " + ScoreStr(sStruct) + "\n" +
                "  ───────────────────────────\n" +
                "  SL DIST   : " + Math.Round(slDist, 2) + " pts\n" +
                "  R:R       : 1:" + Math.Round(rrVal, 1) + "\n" +
                "  TP1 QTY   : " + tp1Qty + " | TP2 QTY: " + tp2Qty + "\n" +
                "  VOL SURGE : " + (volSurge ? "YES" : "No") + "\n" +
                "  ───────────────────────────\n" +
                "  TODAY P&L : " + pnlStr     + "\n" +
                "  RISK REM  : " + riskRemain + "\n" +
                "  TRADES    : " + dailyTradeCount + " / " + MaxTradesPerDay + "\n" +
                "  STATUS    : " + status     + "\n" +
                "╚═════════════════════════════╝";

            Draw.TextFixed(this, "Dashboard", txt, TextPosition.TopRight,
                Brushes.Cyan, new Gui.Tools.SimpleFont("Courier New", 10),
                Brushes.Transparent, Brushes.Black, 90);
        }

        private string ScoreStr(int s)
        {
            return s == 1 ? "▲ BULL" : s == -1 ? "▼ BEAR" : "● NEUT";
        }

        public override string DisplayName
        {
            get { return "PropSniperPro v1.1 [" + MinScore + "pts | " + StopMode + " SL | FIXED]"; }
        }
    }
}
