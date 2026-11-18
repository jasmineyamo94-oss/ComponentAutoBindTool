"""经用户授权，将项目字体临时注册到当前用户；退出时撤销本次注册。"""
import ctypes
from ctypes import wintypes
from contextlib import contextmanager
from pathlib import Path
import winreg


@contextmanager
def temporary_toxia_font():
    path = Path("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf").resolve(strict=True)
    registry_path = r"Software\Microsoft\Windows NT\CurrentVersion\Fonts"
    value_name = "TOXIA (TrueType)"
    gdi = ctypes.WinDLL("gdi32")
    gdi.AddFontResourceExW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.LPVOID]
    gdi.AddFontResourceExW.restype = ctypes.c_int
    gdi.RemoveFontResourceExW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.LPVOID]
    gdi.RemoveFontResourceExW.restype = wintypes.BOOL
    user = ctypes.WinDLL("user32")
    user.SendMessageTimeoutW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM,
                                       wintypes.LPARAM, wintypes.UINT, wintypes.UINT,
                                       ctypes.POINTER(ctypes.c_size_t)]
    user.SendMessageTimeoutW.restype = wintypes.LPARAM

    def notify_fonts():
        result = ctypes.c_size_t()
        user.SendMessageTimeoutW(0xffff, 0x001d, 0, 0, 2, 1000, ctypes.byref(result))

    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry_path, 0,
                        winreg.KEY_QUERY_VALUE | winreg.KEY_SET_VALUE) as key:
        try:
            winreg.QueryValueEx(key, value_name)
        except FileNotFoundError:
            pass
        else:
            raise RuntimeError("TOXIA 已有用户字体注册，拒绝覆盖")
        # 使用已有字体的绝对路径，不复制、删除或改写字体文件。
        loaded = 0
        registered = False
        try:
            winreg.SetValueEx(key, value_name, 0, winreg.REG_SZ, str(path))
            registered = True
            loaded = gdi.AddFontResourceExW(str(path), 0, None)
            if not loaded:
                raise RuntimeError("Windows 临时字体加载失败")
            notify_fonts()
            yield str(path)
        finally:
            try:
                if registered:
                    current, kind = winreg.QueryValueEx(key, value_name)
                    if current != str(path) or kind != winreg.REG_SZ:
                        raise RuntimeError("字体注册已被外部修改，拒绝删除；需人工核对")
                    winreg.DeleteValue(key, value_name)
            finally:
                if loaded:
                    gdi.RemoveFontResourceExW(str(path), 0, None)
                notify_fonts()


if __name__ == "__main__":
    import win32com.client
    with temporary_toxia_font():
        app = win32com.client.GetActiveObject("Photoshop.Application")
        print(app.DoJavaScript('''(function(){var a=[];for(var i=0;i<app.fonts.length;i++){
            var f=app.fonts[i];if(/toxia/i.test(f.name+f.postScriptName))a.push(f.name+"|"+f.postScriptName);}
            return "Photoshop TOXIA: "+a.join(",");})();'''), flush=True)
    print("本次临时字体注册已撤销", flush=True)
