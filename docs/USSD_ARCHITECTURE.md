# USSD Architecture & Hardware Limitations on Windows WWAN Modems

This document records the telecommunications and hardware architecture regarding USSD (Unstructured Supplementary Service Data) on Windows PCs equipped with mobile broadband (WWAN) modems, specifically the **Intel XMM 7360 / Fibocom L850-GL** (common on ThinkPad T480s, HP EliteBook, and Dell Latitude laptops).

---

## 1. Executive Summary

- **Live Interactive USSD Flash Popups (e.g. `*804#`, `*704#` raw balance dialogs) are hardware/firmware unsupported on the Intel XMM 7360 / Fibocom L850-GL modem.**
- The modem **successfully transmits the USSD uplink code** across the cellular network. The carrier (Ethio Telecom / Safaricom) receives it, which is why the carrier dispatches the subsequent bundle balance SMS from shortcode `251994`.
- However, the modem's baseband firmware **lacks a Circuit-Switched (CS) / IMS telephony session engine** to decode and maintain the incoming interactive downstream USSD dialog session (`+CUSD: 2` / `OtherLocalClient` / device busy).
- In Windows Quick Settings, clicking the **Cellular** tile turns off the physical modem transceiver (`RadioState.Off`), which powers down the SIM card reader. To only pause internet data while keeping the SIM alive, you must use Windows Settings -> Network & Internet -> Cellular -> Data connection.

---

## 2. Mobile Phone vs. PC WWAN Modem Architecture

| Feature | Smartphone (Android / iOS) | PC WWAN Modem (Intel XMM 7360 / Fibocom L850-GL) |
| :--- | :--- | :--- |
| **Device Class** | `Voice-Centric` (Full Telephony) | `Voice class: No voice` (Data-Only WWAN card) |
| **Radio Layers** | Full CS Voice, VoLTE, VoNR, RIL framework | High-speed Packet Data (LTE-A, HSPA+), MBIM control |
| **USSD Session Handling** | Dedicated Radio Interface Layer (RIL) handles `RIL_UNSOL_ON_USSD` and renders OS system dialogs | Windows `wmbclass.sys` (MBIM). Modem firmware aborts downstream CS session state machine |
| **SMS Handling** | SMS over NAS / IMS | SMS over NAS / SGs (Fully functional; never drops LTE data) |

---

## 3. Why the Carrier Sends an SMS, but the USSD Popup Fails

When dialing `*804#` (Ethio Telecom) or `*704#` (Safaricom):

1. **Uplink Transmission (Success):**
   - The modem transmits the USSD request string over the radio.
   - The carrier's Mobile Switching Center (MSC) and Home Location Register (HLR) receive the command.
   - Because Ethio Telecom's backend processes `*804#`, it dispatches an automated SMS from `251994` containing voice/data package balances.
   - That SMS arrives in SUPERDOC SMS Manager because SMS over LTE is 100% operational.

2. **Downstream Interactive USSD Flash (Failure):**
   - On a phone, the carrier simultaneously sends a Phase 2 USSD dialog packet over the Circuit-Switched channel:
     ```
     Balance: 0
     Expires on: 23/11/2027
     Win 80k Birr weekly and 150k Birr monthly and more!
     Reply 1 to activate.
     [Cancel]  [Send]
     ```
   - On the Intel XMM 7360 modem, the modem firmware is incapable of holding the bidirectional CS dialog session open. It immediately drops the session and returns `OtherLocalClient` / `NotRegistered` / error code 2 to Windows MBIM.
   - This limitation is documented across both Windows and Linux (`xmm7360-pci` open-source driver project).

---

## 4. Why Turning Off Cellular in Windows Disables SIM Detection

In Windows 10/11:
- The **Cellular** tile in the Quick Settings flyout (Action Center) controls the **hardware/software radio power state** (`RadioState.On` vs `RadioState.Off`), identical to Airplane Mode for WWAN.
- When turned off, the PC cuts power to the modem transceiver, shutting down communication with the physical SIM tray.
- WinRT APIs (`MobileBroadbandAccount.AvailableNetworkAccountIds`) return 0 accounts, causing the app to report:
  `Failed to create the USSD session; the device is busy or unavailable`.

---

## 5. Conclusion & Best Practices

1. **Do not attempt to force interactive USSD popups on this modem:** The Intel XMM 7360 baseband firmware cannot decode or maintain the downstream session.
2. **Account Balance Alternatives:**
   - Use the carrier's self-service app (e.g. Telebirr for Ethio Telecom, M-PESA for Safaricom).
   - Use the carrier's web portal.
   - Rely on SMS shortcodes and carrier notifications (which arrive cleanly in SUPERDOC SMS Manager).
