using System.Security.Cryptography.X509Certificates;
using Communication.Network.RUDP;

namespace DRPC.Shared.Network;

/// <summary>
/// 접속·수신 끝점 전송 옵션 묶음 — <see cref="HubSessionFactory.CreateTransportOptions"/> 로
/// 전송 스택 옵션으로 변환된다. 연결 타임아웃·연결 상한과 달리 끝단 공유값이므로 묶음 타입으로 제공한다.
/// </summary>
public sealed class RpcEndpointOptions
{
    /// <summary>접속 키. null/빈 문자열이면 전송 스택 기본 키.</summary>
    public string? ConnectionKey { get; set; }

    /// <summary>
    /// 클라이언트 연결 시도 상한(ms). 침묵 호스트(블랙홄) 연결 실패를 이 시간 이내로 확정한다.
    /// 0(기본)이면 전송 스택 기본값(약 5초). 음수는 거부.
    /// </summary>
    public int ConnectTimeoutMs { get; set; }

    /// <summary>
    /// 서버 동시 수락 연결 상한. 상한 도달 시 접속 요청은 즉시 거부되고 수락은 계속된다(연결 고갈 공격 방어).
    /// 0(기본)이면 무제한. 음수는 거부.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// 패킷 무결성 검사(CRC32c) 활성화 — 송신마다 체크섬(4바이트)을 붙이고 수신은 위반 패킷을 프로토콜 처리 전에 폐기한다
    /// (Communication <c>RudpTransportOptions.Crc32cEnabled</c>). <b>양단 모두 같은 설정</b>이어야 한다(와이어 비호환).
    /// 위변조 검출뿐 방지가 아니다(키 없는 CRC — 능동 공격자는 재계산 가능). 기밀성·인증은 없다. 기본 <c>false</c>.
    /// </summary>
    public bool EnableCrc32c { get; set; }

    /// <summary>
    /// 서버 측 DTLS 인증서 — 설정 시 이 끝점의 패킷이 DTLS 1.2로 암호화된다(Communication 2.5.0 <c>RudpTransportOptions.Tls</c>).
    /// 서버 역할에서만 사용된다. <b>TLS 필드를 하나라도 설정하면 양단 모두 암호화 모드여야 한다</b>(와이어 비호환 - 평문 끝단은 RUDP 연결까진 성공하지만 어떤 RPC 도 완료되지 않는다).
    /// 기본 <c>null</c> = 평문(기존 동작 유지).
    /// </summary>
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// 클라이언트 측 서버 인증서 검증 — 대상 호스트명(SAN/CN 일치). <see cref="ServerCertificate"/> 를 가진 서버에 접속할 때 설정한다.
    /// 이름 전용 매칭은 같은 이름의 자체서명 인증서로 중간자가 통과할 수 있어 Communication 2.7.0 부터 옵트인제다 —
    /// <see cref="TlsAllowNameOnlyCertificateMatch"/> 도 함께 설정해야 수용된다(미설정 시 핸드셰이크 거부, fail-closed).
    /// 옵트인 시 만료 인증서(NotBefore/NotAfter) 도 거부된다. 실서비스는 <see cref="TlsCertificateValidation"/> 핀닝 권장.
    /// </summary>
    public string? TlsTargetHost { get; set; }

    /// <summary>
    /// 이름 전용 인증서 매칭(<see cref="TlsTargetHost"/>) 옵트인 — Communication 2.7.0
    /// <c>RudpTlsOptions.AllowNameOnlyCertificateMatch</c> 전달. 기본 <c>false</c>(fail-closed, 전송 스택과 동일).
    /// 핀닝(<see cref="TlsCertificateValidation"/>) 사용 시 무의미하다.
    /// </summary>
    public bool TlsAllowNameOnlyCertificateMatch { get; set; }

    /// <summary>
    /// 클라이언트 측 서버 인증서 검증 — 핀닝 콜백(DER 바이트 → 신뢰 여부). <see cref="RudpTlsOptions.GetSha256Fingerprint(byte[])"/>
    /// 로 지문 비교 권장(게임 표준 경로). 무조건 통과 콜백 금지.
    /// </summary>
    public RudpRemoteCertificateValidation? TlsCertificateValidation { get; set; }

    /// <summary>
    /// 전송 스택 옵션으로 변환한다 — 필드별 계약(음수 거부·0=미설정)은 <see cref="HubSessionFactory.CreateTransportOptions"/> 매개변수 버전과 동일.
    /// </summary>
    public RudpTransportOptions ToTransportOptions()
        => HubSessionFactory.CreateTransportOptions(
            ConnectionKey, ConnectTimeoutMs, MaxConnections, EnableCrc32c, TlsOptions);

    /// <summary>설정된 TLS 필드로 <see cref="RudpTlsOptions"/> 를 조립한다. 미설정이면 <c>null</c>(평문).</summary>
    private RudpTlsOptions? TlsOptions
        => ServerCertificate is null && TlsTargetHost is null && TlsCertificateValidation is null
            ? null
            : new RudpTlsOptions
            {
                ServerCertificate = ServerCertificate,
                TargetHost = TlsTargetHost,
                RemoteCertificateValidation = TlsCertificateValidation,
                AllowNameOnlyCertificateMatch = TlsAllowNameOnlyCertificateMatch,
            };
}
