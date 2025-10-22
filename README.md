<div align="center">
<p><img src="./readme/logo.png" height="150"></p> 

# TUN324

[![Stars](https://img.shields.io/github/stars/snltty/tun324?style=flat)](https://github.com/snltty/tun324)
[![Forks](https://img.shields.io/github/forks/snltty/tun324?style=flat)](https://github.com/snltty/tun324)
[![Release](https://img.shields.io/github/v/release/snltty/tun324?sort=semver)](https://github.com/snltty/tun324/releases)
[![License](https://img.shields.io/github/license/snltty/tun324)](https://mit-license.org/)

<a href="https://jq.qq.com/?_wv=1027&k=ucoIVfz4" target="_blank">加入组织：1121552990</a>

</div>

# Layer 3 to Layer 4
High-performance, TUN device  Layer 3 IP packets redirect  to  Layer 4 TCP socket. 高性能，tun 设备三层IP包重定向为四层socket。

# 参数

```
tun324.exe --name tun324 --ip 10.18.18.2/24 --proxy socks5://127.0.0.1:1080 
```

1. **--name tun324** 名称
1. **--ip 10.18.18.2/24** ip掩码
1. **--guid 2ef1a78e-9579-4214-bbc1-5dc556b59042** windows wintun 的GUID，默认 2ef1a78e-9579-4214-bbc1-5dc556b59042
1. **--mtu 1420** MTU
1. **--proxy socks5://127.0.0.1:1080** 代理地址
1. **--route** 路由