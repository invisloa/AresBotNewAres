Copy-Item "C:\Users\jakub.olowiak\source\repos\stare\Ares\SSDrv\build\km\km.sys" `
          "C:\Windows\System32\drivers\km.sys" -Force

sc.exe start SexyDriver

$drv="SexyDriver"
$src="C:\Users\jakub.olowiak\source\repos\stare\Ares\SSDrv\build\km\km.sys"
$dst="C:\Windows\System32\drivers\km.sys"

Copy-Item $src $dst -Force
sc.exe start $drv



sc.exe stop SexyDriver
Copy-Item $src $dst -Force
sc.exe start SexyDriver
               


