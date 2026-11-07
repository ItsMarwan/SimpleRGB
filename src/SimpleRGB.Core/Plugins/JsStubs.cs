namespace SimpleRGB.Core.Plugins;

/// <summary>
/// Stand-ins for SignalRGB's built-in "@SignalRGB/..." modules and global helper objects.
/// They give plugins sensible answers without granting any extra capability.
/// </summary>
internal static class JsStubs
{
    /// <summary>Evaluated in every plugin engine before the plugin itself.</summary>
    public const string Prelude = """
        globalThis.__makeStub = function (label) {
            const warned = new Set();
            return new Proxy({}, {
                get(target, prop) {
                    if (typeof prop === 'symbol' || prop in target) return target[prop];
                    if (prop === 'then') return undefined; // never look like a Promise
                    return function () {
                        if (!warned.has(prop)) { warned.add(prop); __hostWarn(label + '.' + String(prop) + ' is not supported by SimpleRGB'); }
                        return undefined;
                    };
                }
            });
        };
        globalThis.__wrapHost = function (host, label) {
            const warned = new Set();
            return new Proxy(host, {
                get(target, prop) {
                    if (typeof prop === 'symbol' || prop in target) return target[prop];
                    if (prop === 'then') return undefined;
                    return function () {
                        if (!warned.has(prop)) { warned.add(prop); __hostWarn(label + '.' + String(prop) + '() is not supported by SimpleRGB (ignored)'); }
                        return undefined;
                    };
                }
            });
        };
        globalThis.console = {
            log: (...a) => __hostLog(a.map(x => typeof x === 'string' ? x : __fmt(x)).join(' ')),
            warn: (...a) => __hostLog(a.map(x => typeof x === 'string' ? x : __fmt(x)).join(' ')),
            error: (...a) => __hostLog(a.map(x => typeof x === 'string' ? x : __fmt(x)).join(' ')),
            info: (...a) => __hostLog(a.map(x => typeof x === 'string' ? x : __fmt(x)).join(' ')),
        };
        globalThis.__fmt = function (x) {
            if (Array.isArray(x)) return '[' + x.join(', ') + ']';
            if (x !== null && typeof x === 'object') { try { return JSON.stringify(x); } catch (e) { return String(x); } }
            return String(x);
        };
        globalThis.battery = __makeStub('battery');
        globalThis.keyboard = __makeStub('keyboard');
        globalThis.mouse = __makeStub('mouse');
        globalThis.udp = __makeStub('udp');
        globalThis.dtls = __makeStub('dtls');
        globalThis.base64 = {
            Encode(arr) { let s = ''; for (const b of arr) s += String.fromCharCode(b & 255); return btoaPoly(s); },
            Decode(str) { const s = atobPoly(str); const out = []; for (let i = 0; i < s.length; i++) out.push(s.charCodeAt(i)); return out; },
        };
        const __b64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
        function btoaPoly(s) {
            let out = '';
            for (let i = 0; i < s.length; i += 3) {
                const a = s.charCodeAt(i), b = s.charCodeAt(i + 1), c = s.charCodeAt(i + 2);
                const n = (a << 16) | ((b || 0) << 8) | (c || 0);
                out += __b64[(n >> 18) & 63] + __b64[(n >> 12) & 63] + (isNaN(b) ? '=' : __b64[(n >> 6) & 63]) + (isNaN(c) ? '=' : __b64[n & 63]);
            }
            return out;
        }
        function atobPoly(str) {
            const clean = String(str).replace(/[^A-Za-z0-9+/]/g, '');
            let out = '';
            for (let i = 0; i < clean.length; i += 4) {
                const n = (__b64.indexOf(clean[i]) << 18) | (__b64.indexOf(clean[i + 1]) << 12) |
                          ((__b64.indexOf(clean[i + 2]) & 63) << 6) | (__b64.indexOf(clean[i + 3]) & 63);
                out += String.fromCharCode((n >> 16) & 255);
                if (clean[i + 2] !== undefined) out += String.fromCharCode((n >> 8) & 255);
                if (clean[i + 3] !== undefined) out += String.fromCharCode(n & 255);
            }
            return out;
        }
        """;

    public const string ErrorsModule = """
        export class ContextError extends Error {
            constructor(message, context) { super(message); this.name = 'ContextError'; this.context = context; }
        }
        const __ctx = new Map();
        export const globalContext = {
            set(key, value) { __ctx.set(key, value); },
            get(key) { return __ctx.get(key); },
            delete(key) { __ctx.delete(key); },
            clear() { __ctx.clear(); },
        };
        export const Assert = {
            isOk(condition, message) { if (!condition) throw new Error(message || 'Assertion failed'); return condition; },
            isEqual(a, b, message) { if (a !== b) throw new Error(message || ('Assertion failed: ' + a + ' !== ' + b)); return true; },
            fail(message) { throw new Error(message || 'Assertion failed'); },
            softIsDefined(value, message) {
                if (value === undefined || value === null) { __hostLog(message || 'Value is not defined'); return false; }
                return true;
            },
            unreachable(message) { throw new Error(message || 'Unreachable code reached'); },
        };
        export default { Assert, ContextError, globalContext };
        """;

    public const string SystemInfoModule = """
        export default {
            GetMotherboardInfo() { return { model: __sys.model, manufacturer: __sys.manufacturer, product: __sys.model, name: __sys.model }; },
            GetBiosInfo() { return { vendor: __sys.biosVendor, version: __sys.biosVersion }; },
            GetRamInfo() { return undefined; },
        };
        """;

    // Partner "permissions" are an online SignalRGB account feature. Every lookup returns an empty grant set.
    public const string PermissionsModule = """
        export default {
            permissions() { return new Proxy({}, { get(t, p) { return typeof p === 'symbol' ? undefined : {}; } }); },
            setCallback() { },
        };
        """;

    public static string GenericModule(string name) => $$"""
        const stub = __makeStub('{{name.Replace("'", "")}}');
        export default stub;
        """;
}
