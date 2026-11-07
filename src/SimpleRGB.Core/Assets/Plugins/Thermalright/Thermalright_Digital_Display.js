// SimpleRGB built-in plugin: Thermalright "Digital" cooler display (USB 0416:8001), PA120 DIGITAL layout.
//
// Protocol matches Thermalright's own TRCC app (LED style 2):
//   identify:  DA DB DC DD 00×8 01 …            → reply byte[5] = model (16..31 = PA120 DIGITAL family)
//   frame:     DA DB DC DD 00×8 02 00 00 00 FC 00 00 00  + 84 LEDs × RGB (252 bytes) in the display's wiring order,
//              sent as 64-byte HID reports. Colors are capped at 40% like TRCC does.
// All 84 LEDs are the display itself: "CPU"/"GPU" labels, °C/°F, % and 7-segment digits showing
// CPU temperature + usage (top row) and GPU temperature + usage (bottom row).

export function Name() { return "Thermalright Digital Display"; }
export function VendorId() { return 0x0416; }
export function ProductId() { return [0x8001]; }
export function Publisher() { return "SimpleRGB"; }
export function Type() { return "Hid"; }
export function DeviceType() { return "cooler"; }
export function Size() { return [40, 12]; }
export function ConflictingProcesses() { return ["TRCC.exe"]; }
// Blank the display whenever SimpleRGB stops, so it never shows frozen temperatures.
export function AlwaysShutdown() { return true; }
// TRCC drives the same display; never write while it runs (it would flicker between the two).
export function PauseWhenConflicting() { return true; }

/* global TempUnit:readonly ShowRows:readonly ScreenRefresh:readonly ReadingsInterval:readonly Smoothing:readonly
   DigitColors:readonly CoolTemp:readonly HotTemp:readonly ShowLabels:readonly */
export function ControllableParameters() {
	return [
		{ property: "ShowRows", group: "display", label: "Show", type: "combobox", values: ["CPU and GPU", "CPU only", "GPU only"], default: "CPU and GPU",
			description: "Which rows of the display are used." },
		{ property: "TempUnit", group: "display", label: "Temperature unit", type: "combobox", values: ["Celsius", "Fahrenheit"], default: "Celsius" },
		{ property: "ReadingsInterval", group: "display", label: "Update readings", type: "combobox",
			values: ["Every 0.25 s", "Every 0.5 s", "Every 1 s", "Every 2 s"], default: "Every 1 s",
			description: "How often the temperature and usage numbers refresh (sensor polling rate)." },
		{ property: "ScreenRefresh", group: "display", label: "Screen refresh", type: "combobox",
			values: ["4 fps", "10 fps", "20 fps", "30 fps"], default: "10 fps",
			description: "Frames per second sent to the display. Effects always run smoothly; this keeps static colors and numbers crisp." },
		{ property: "Smoothing", group: "display", label: "Smooth numbers", type: "combobox", values: ["Off", "Light", "Strong"], default: "Light",
			description: "Averages readings so the digits don't jump around." },
		{ property: "DigitColors", group: "display", label: "Digit colors", type: "combobox", values: ["Lighting effect", "By temperature"], default: "Lighting effect",
			description: "By temperature: green when cool, yellow when warm, red when hot (each row uses its own temperature)." },
		{ property: "CoolTemp", group: "display", label: "Cool below (°C)", type: "number", min: 20, max: 80, step: 1, default: 50 },
		{ property: "HotTemp", group: "display", label: "Hot above (°C)", type: "number", min: 40, max: 105, step: 1, default: 80 },
		{ property: "ShowLabels", group: "display", label: "Show CPU / GPU labels", type: "boolean", default: "true" },
	];
}

export function Validate(endpoint) {
	return endpoint.usage_page === 0xff00;
}

// ------------------------------------------------------------------ layout

// 7-segment font: which of A,B,C,D,E,F,G are lit for 0-9 (same as TRCC).
const FONT = {
	0: "ABCDEF", 1: "BC", 2: "ABDEG", 3: "ABCDG", 4: "BCFG",
	5: "ACDFG", 6: "ACDEFG", 7: "ABC", 8: "ABCDEFG", 9: "ABCDFG",
};
const SEGMENTS = ["A", "B", "C", "D", "E", "F", "G"];
// Segment positions inside a 3×5 digit cell (used for effects that flow across the display).
const SEG_POS = { A: [1, 0], B: [2, 1], C: [2, 3], D: [1, 4], E: [0, 3], F: [0, 1], G: [1, 2] };

// LEDs in visual order (left → right, top row then bottom row). name → [x, y]
const LEDS = [];
function add(name, x, y) { LEDS.push({ name, x, y }); }
function addDigit(n, x, y) { for (const s of SEGMENTS) add(s + n, x + SEG_POS[s][0], y + SEG_POS[s][1]); }

// Top row: CPU  [temp digits 1-3] °C °F  [1][digits 4-5] %
add("Cpu1", 0, 1); add("Cpu2", 0, 3);
addDigit(1, 2, 0); addDigit(2, 6, 0); addDigit(3, 10, 0);
add("SSD", 14, 1); add("HSD", 14, 3);
add("B11", 17, 1); add("C11", 17, 3);
addDigit(4, 19, 0); addDigit(5, 23, 0);
add("BFB", 27, 2);
// Bottom row: GPU  [temp digits 6-8] °C °F  [1][digits 9-10] %
add("Gpu1", 0, 7); add("Gpu2", 0, 9);
addDigit(6, 2, 6); addDigit(7, 6, 6); addDigit(8, 10, 6);
add("SSD1", 14, 7); add("HSD1", 14, 9);
add("B12", 17, 7); add("C12", 17, 9);
addDigit(9, 19, 6); addDigit(10, 23, 6);
add("BFB1", 27, 8);

const INDEX = {};
LEDS.forEach((l, i) => { INDEX[l.name] = i; });

// Order the controller expects the 84 colors in (from TRCC, PA120 DIGITAL).
const WIRING = [
	"Cpu2", "Cpu1", "F1", "A1", "B1", "G1", "E1", "D1", "C1", "F2", "A2", "B2", "G2", "E2", "D2", "C2",
	"F3", "A3", "B3", "G3", "E3", "D3", "C3", "SSD", "HSD", "C11", "B11", "F4", "A4", "B4", "G4", "E4", "D4", "C4",
	"F5", "A5", "B5", "G5", "E5", "D5", "C5", "BFB", "BFB1", "C10", "D10", "E10", "G10", "B10", "A10", "F10",
	"C9", "D9", "E9", "G9", "B9", "A9", "F9", "B12", "C12", "SSD1", "HSD1", "C8", "D8", "E8", "G8", "B8", "A8", "F8",
	"C7", "D7", "E7", "G7", "B7", "A7", "F7", "C6", "D6", "E6", "G6", "B6", "A6", "F6", "Gpu1", "Gpu2",
];

const LED_COUNT = 84;
const BRIGHTNESS_CAP = 0.4; // TRCC never drives these LEDs above 40%
const HEADER = [0xDA, 0xDB, 0xDC, 0xDD, 0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0, 0, 0, LED_COUNT * 3, 0, 0, 0];

let supported = false;
let model = -1;

export function LedNames() { return LEDS.map(l => l.name); }
export function LedPositions() { return LEDS.map(l => [l.x, l.y]); }

export function Initialize() {
	device.setControllableLeds(LEDS.map(l => l.name), LEDS.map(l => [l.x, l.y]));
	device.setSize([30, 11]);

	model = Identify();
	supported = model >= 16 && model < 32; // PA120 DIGITAL family uses this exact layout
	if (supported) {
		applySettings();
		device.setName("Thermalright PA120 Digital Display");
		device.log(`Thermalright display model ${model} detected.`);
	} else {
		device.setName("Thermalright Digital Display (unsupported model)");
		device.notify("Display model not supported yet",
			`This Thermalright display reported model ${model}. SimpleRGB leaves it untouched to be safe.`, 0);
	}
}

function Identify() {
	device.clearReadBuffer();
	const query = [0x00, 0xDA, 0xDB, 0xDC, 0xDD, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
	device.write(query, 65);
	for (let attempt = 0; attempt < 3; attempt++) {
		const reply = device.read([0x00], 64, 1000);
		if (device.getLastReadSize() > 6 && reply[0] === 0xDA && reply[1] === 0xDB && reply[2] === 0xDC && reply[3] === 0xDD) {
			return reply[5];
		}
	}
	return -1;
}

// ------------------------------------------------------------------ rendering

function setDigit(on, n, value) {
	const segs = value === "-" ? "G" : value === null ? "" : FONT[value];
	for (const s of SEGMENTS) on[INDEX[s + n]] = segs.includes(s);
}

// Temperature: 3 digits, leading zeros hidden (like TRCC). Unknown → "- - -".
function setTemp(on, first, value) {
	if (value < 0) { setDigit(on, first, "-"); setDigit(on, first + 1, "-"); setDigit(on, first + 2, "-"); return; }
	value = Math.min(999, Math.round(value));
	const h = Math.floor(value / 100), t = Math.floor(value / 10) % 10, o = value % 10;
	setDigit(on, first, h === 0 ? null : h);
	setDigit(on, first + 1, h === 0 && t === 0 ? null : t);
	setDigit(on, first + 2, o);
}

// Usage: optional leading "1" (for 100) + 2 digits. Unknown → "- -".
function setUsage(on, oneB, oneC, tensDigit, value) {
	if (value < 0) {
		on[INDEX[oneB]] = false; on[INDEX[oneC]] = false;
		setDigit(on, tensDigit, "-"); setDigit(on, tensDigit + 1, "-");
		return;
	}
	value = Math.max(0, Math.min(100, Math.round(value)));
	const hundred = value === 100;
	on[INDEX[oneB]] = hundred; on[INDEX[oneC]] = hundred;
	const t = Math.floor(value / 10) % 10, o = value % 10;
	setDigit(on, tensDigit, !hundred && t === 0 ? null : t);
	setDigit(on, tensDigit + 1, o);
}

function toUnit(celsius) {
	if (celsius < 0) return -1;
	return TempUnit === "Fahrenheit" ? celsius * 9 / 5 + 32 : celsius;
}

// ------------------------------------------------------------------ readings (held between updates, optionally smoothed)

const readings = { cpuTemp: -1, cpuLoad: -1, gpuTemp: -1, gpuLoad: -1 };
let lastSample = 0;

function intervalMs() {
	return { "Every 0.25 s": 250, "Every 0.5 s": 500, "Every 2 s": 2000 }[ReadingsInterval] || 1000;
}

function applySettings() {
	sensors.setPollInterval(intervalMs());
	device.setMinimumFrameRate(parseInt(ScreenRefresh, 10) || 10);
}

function sample() {
	const now = Date.now();
	if (now - lastSample < intervalMs()) return;
	lastSample = now;
	const alpha = Smoothing === "Strong" ? 0.25 : Smoothing === "Light" ? 0.5 : 1;
	for (const key of Object.keys(readings)) {
		const v = sensors[key]();
		if (v < 0) { readings[key] = -1; continue; }
		readings[key] = readings[key] < 0 ? v : readings[key] + (v - readings[key]) * alpha;
	}
}

function buildLitMask() {
	const on = new Array(LED_COUNT).fill(false);
	const fahrenheit = TempUnit === "Fahrenheit";
	const cpu = ShowRows !== "GPU only";
	const gpu = ShowRows !== "CPU only";
	const labels = ShowLabels !== false;
	if (cpu) {
		on[INDEX.Cpu1] = on[INDEX.Cpu2] = labels;
		on[INDEX.BFB] = true;
		on[INDEX.SSD] = !fahrenheit;
		on[INDEX.HSD] = fahrenheit;
		setTemp(on, 1, toUnit(readings.cpuTemp));
		setUsage(on, "B11", "C11", 4, readings.cpuLoad);
	}
	if (gpu) {
		on[INDEX.Gpu1] = on[INDEX.Gpu2] = labels;
		on[INDEX.BFB1] = true;
		on[INDEX.SSD1] = !fahrenheit;
		on[INDEX.HSD1] = fahrenheit;
		setTemp(on, 6, toUnit(readings.gpuTemp));
		setUsage(on, "B12", "C12", 9, readings.gpuLoad);
	}
	return on;
}

// Green → yellow → red between the cool and hot thresholds (°C).
function temperatureColor(celsius) {
	if (celsius < 0) return [255, 255, 255];
	const cool = Number(CoolTemp) || 50;
	const hot = Math.max(cool + 1, Number(HotTemp) || 80);
	const t = Math.max(0, Math.min(1, (celsius - cool) / (hot - cool)));
	return t < 0.5 ? [Math.round(510 * t), 255, 0] : [255, Math.round(255 - 510 * (t - 0.5)), 0];
}

const TOP_ROW = new Set(LEDS.filter(l => l.y < 6).map(l => l.name));

function sendFrame(colorAt) {
	const packet = HEADER.slice();
	for (const name of WIRING) {
		const c = colorAt(INDEX[name]);
		packet.push(Math.floor(c[0] * BRIGHTNESS_CAP), Math.floor(c[1] * BRIGHTNESS_CAP), Math.floor(c[2] * BRIGHTNESS_CAP));
	}
	// 272 bytes → 64-byte HID reports (report id 0).
	for (let offset = 0; offset < packet.length; offset += 64) {
		device.write([0x00].concat(packet.slice(offset, offset + 64)), 65);
	}
}

export function Render() {
	if (!supported) return;
	sample();
	const on = buildLitMask();
	if (DigitColors === "By temperature") {
		const cpuColor = temperatureColor(readings.cpuTemp);
		const gpuColor = temperatureColor(readings.gpuTemp);
		sendFrame(i => {
			if (!on[i]) return [0, 0, 0];
			// Keep the user's brightness, breathing and lights-off: scale by the effect's intensity.
			const fx = device.color(LEDS[i].x, LEDS[i].y);
			const k = Math.max(fx[0], fx[1], fx[2]) / 255;
			const c = TOP_ROW.has(LEDS[i].name) ? cpuColor : gpuColor;
			return [c[0] * k, c[1] * k, c[2] * k];
		});
		return;
	}
	sendFrame(i => on[i] ? device.color(LEDS[i].x, LEDS[i].y) : [0, 0, 0]);
}

export function onReadingsIntervalChanged() { lastSample = 0; applySettings(); }
export function onScreenRefreshChanged() { applySettings(); }

export function Shutdown() {
	if (!supported) return;
	sendFrame(() => [0, 0, 0]);
}
