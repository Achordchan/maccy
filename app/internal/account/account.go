// Package account is what the preferences window does with the server's
// account: sign in and out, register, the password code flows, the
// subscription and the card redemption. It is the account half of
// PreferencesWindowViewModel without the window: every call returns the text
// the C# client shows in its toast, so a window only has to show it.
package account

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"klip/internal/model"
	"klip/internal/syncclient"
)

// Store is the settings, as the account needs them.
type Store interface {
	Get() model.Settings
	Update(func(*model.Settings)) error
}

// Service talks to the server for the signed-in user.
type Service struct {
	Client *syncclient.Client
	Store  Store
	// Now is time.Now unless a test sets it.
	Now func() time.Time
}

func (s *Service) now() time.Time {
	if s.Now != nil {
		return s.Now()
	}
	return time.Now()
}

// Timeouts of the C# flows.
const (
	signInTimeout   = 3 * time.Minute
	passwordTimeout = 2 * time.Minute
)

// SignedIn is HasSignedInSession: an access token with more than 30 seconds
// left, or a refresh token.
func SignedIn(st model.Settings, now time.Time) bool {
	if strings.TrimSpace(model.Str(st.AuthAccessToken)) != "" && st.AuthExpiresAtUnixMs > now.UnixMilli()+30_000 {
		return true
	}
	return strings.TrimSpace(model.Str(st.AuthRefreshToken)) != ""
}

// SignedIn says whether the user is signed in now.
func (s *Service) SignedIn() bool { return SignedIn(s.Store.Get(), s.now()) }

// Email is the address shown as signed in, "" when not.
func (s *Service) Email() string {
	st := s.Store.Get()
	if !SignedIn(st, s.now()) {
		return ""
	}
	return strings.TrimSpace(model.Str(st.AuthUserEmail))
}

// StatusText is "未登录", "已登录" or "已登录（邮箱）".
func (s *Service) StatusText() string {
	if !s.SignedIn() {
		return "未登录"
	}
	if e := s.Email(); e != "" {
		return "已登录（" + e + "）"
	}
	return "已登录"
}

func (s *Service) storeTokens(t syncclient.TokenSet, fallbackEmail string) {
	email := t.Email
	if strings.TrimSpace(email) == "" {
		email = fallbackEmail
	}
	_ = s.Store.Update(func(m *model.Settings) {
		m.AuthAccessToken = &t.AccessToken
		m.AuthRefreshToken = &t.RefreshToken
		m.AuthIdToken = &t.IDToken
		m.AuthExpiresAtUnixMs = t.ExpiresAt.UnixMilli()
		m.AuthUserEmail = &email
		m.NasAgentBaseUrl = model.OfficialSyncBaseUrl
	})
}

// Result is what a call did: Message is for the toast, OK says whether it
// worked.
type Result struct {
	OK      bool
	Message string
}

func fail(msg string) Result { return Result{Message: msg} }

// SignIn is LoginAsync. A blank email or password is refused before any call.
func (s *Service) SignIn(ctx context.Context, email, password string) Result {
	return s.enter(ctx, email, password, "登录成功", "登录失败", "登录已取消", s.Client.Login)
}

// Register is RegisterAsync: the server signs the new account in.
func (s *Service) Register(ctx context.Context, email, password string) Result {
	return s.enter(ctx, email, password, "注册成功", "注册失败", "已取消", s.Client.Register)
}

func (s *Service) enter(ctx context.Context, email, password, okMsg, failMsg, cancelMsg string,
	call func(context.Context, string, string) (syncclient.TokenSet, error)) Result {
	email = strings.TrimSpace(email)
	if email == "" || strings.TrimSpace(password) == "" {
		return fail("请输入邮箱和密码")
	}
	ctx, cancel := context.WithTimeout(ctx, signInTimeout)
	defer cancel()
	t, err := call(ctx, email, password)
	if err != nil {
		return s.failure(err, failMsg, cancelMsg)
	}
	s.storeTokens(t, email)
	return Result{OK: true, Message: okMsg}
}

// SignOut forgets the tokens and the address.
func (s *Service) SignOut() Result {
	_ = s.Store.Update(func(m *model.Settings) { syncclient.ClearAuthSession(m, true) })
	return Result{OK: true, Message: "已退出登录"}
}

// EnsureAccessToken is EnsureAccessTokenAsync: a token that is still good is
// checked with the server (a refused one signs the user out), else the
// refresh token is used. "" means signed out.
func (s *Service) EnsureAccessToken(ctx context.Context) (string, error) {
	cur := s.Store.Get()
	access := strings.TrimSpace(model.Str(cur.AuthAccessToken))
	if access != "" && cur.AuthExpiresAtUnixMs > s.now().UnixMilli()+30_000 {
		email, err := s.Client.CurrentEmail(ctx, access)
		if err != nil {
			// No answer (offline, a reset connection) says nothing about the
			// login: it stays, and the caller is told to try again later.
			if ctx.Err() != nil {
				return "", ctx.Err()
			}
			return "", err
		}
		if strings.TrimSpace(email) != "" {
			return access, nil
		}
		s.clearSession(model.Str(cur.AuthRefreshToken), access)
		return "", nil
	}
	refresh := strings.TrimSpace(model.Str(cur.AuthRefreshToken))
	if refresh == "" {
		return "", nil
	}
	t, err := s.Client.Refresh(ctx, refresh)
	if err != nil {
		if ctx.Err() != nil {
			return "", ctx.Err()
		}
		var ae *syncclient.APIError
		if !errors.As(err, &ae) || !refused(ae.StatusCode) {
			// No answer, or a server that is busy or failing (5xx, 429):
			// not a reason to sign out.
			return "", err
		}
		s.clearSession(refresh, access)
		return "", nil
	}
	// The user may have signed out, or in as somebody else, while the request
	// was on its way: the answer belongs to the session that asked.
	same := false
	_ = s.Store.Update(func(m *model.Settings) {
		if strings.TrimSpace(model.Str(m.AuthRefreshToken)) != refresh {
			return
		}
		same = true
		m.AuthAccessToken = &t.AccessToken
		if strings.TrimSpace(t.RefreshToken) != "" {
			m.AuthRefreshToken = &t.RefreshToken
		}
		if strings.TrimSpace(t.IDToken) != "" {
			m.AuthIdToken = &t.IDToken
		}
		m.AuthExpiresAtUnixMs = t.ExpiresAt.UnixMilli()
		if strings.TrimSpace(t.Email) != "" {
			m.AuthUserEmail = &t.Email
		}
		m.NasAgentBaseUrl = model.OfficialSyncBaseUrl
	})
	if !same {
		return "", nil
	}
	return model.Str(s.Store.Get().AuthAccessToken), nil
}

// refused says whether an HTTP status is the server turning the token down
// (as against being unwell).
func refused(status int) bool {
	return status == 400 || status == 401 || status == 403
}

// clearSession signs out, but only the session that was found wanting: if the
// tokens are no longer those, somebody signed in again meanwhile.
func (s *Service) clearSession(refresh, access string) {
	_ = s.Store.Update(func(m *model.Settings) {
		if strings.TrimSpace(model.Str(m.AuthRefreshToken)) != strings.TrimSpace(refresh) ||
			strings.TrimSpace(model.Str(m.AuthAccessToken)) != strings.TrimSpace(access) {
			return
		}
		syncclient.ClearAuthSession(m, false)
	})
}

// Purpose of a password code.
type Purpose string

const (
	Reset  Purpose = "reset"  // forgot password: not signed in
	Change Purpose = "change" // change password: signed in
)

func validEmail(e string) bool { return strings.TrimSpace(e) != "" && strings.Contains(e, "@") }

// SendCode asks for a code by mail. For Change the address is the signed-in
// one; for Reset it is what the user typed. Cooldown is the seconds before
// another code may be asked for.
func (s *Service) SendCode(ctx context.Context, p Purpose, typedEmail string) (cooldown int, r Result) {
	var email, token string
	if p == Change {
		email = strings.TrimSpace(model.Str(s.Store.Get().AuthUserEmail))
		if email == "" {
			email = strings.TrimSpace(typedEmail)
		}
	} else {
		email = strings.TrimSpace(typedEmail)
	}
	if !validEmail(email) {
		return 0, fail("请输入有效邮箱")
	}
	ctx, cancel := context.WithTimeout(ctx, passwordTimeout)
	defer cancel()
	if p == Change {
		var err error
		token, err = s.EnsureAccessToken(ctx)
		if err == nil && token == "" {
			return 0, fail("登录已失效，请重新登录")
		}
		if err != nil {
			return 0, s.failure(err, "发送验证码失败", "发送验证码已取消")
		}
	}
	cd, err := s.Client.SendPasswordCode(ctx, string(p), email, token)
	if err != nil {
		return 0, s.failure(err, "发送验证码失败", "发送验证码已取消")
	}
	return cd, Result{OK: true, Message: "验证码已发送，请检查邮箱"}
}

func (s *Service) failure(err error, failMsg, cancelMsg string) Result {
	// C# catches OperationCanceledException, which a timeout is too.
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return fail(cancelMsg)
	}
	return fail(ExplainAuth(err, failMsg))
}

// validateNewPassword is ValidatePasswordRecoveryInputs.
func validateNewPassword(email, code, pw, confirm string) string {
	switch {
	case !validEmail(email):
		return "请输入有效邮箱"
	case strings.TrimSpace(code) == "":
		return "请输入验证码"
	case len(pw) < 8:
		return "新密码至少 8 位"
	case pw != confirm:
		return "两次输入的新密码不一致"
	}
	return ""
}

// ResetPassword sets a new password with the code from the mail, for a user
// who is not signed in.
func (s *Service) ResetPassword(ctx context.Context, email, code, pw, confirm string) Result {
	email = strings.TrimSpace(email)
	if msg := validateNewPassword(email, code, pw, confirm); msg != "" {
		return fail(msg)
	}
	ctx, cancel := context.WithTimeout(ctx, passwordTimeout)
	defer cancel()
	if err := s.Client.ResetPassword(ctx, email, code, pw); err != nil {
		return s.failure(err, "找回密码失败", "找回密码已取消")
	}
	s.afterPasswordUpdated(email)
	return Result{OK: true, Message: "密码已更新，请使用新密码重新登录"}
}

// ChangePassword sets a new password for the signed-in user.
func (s *Service) ChangePassword(ctx context.Context, typedEmail, code, pw, confirm string) Result {
	email := strings.TrimSpace(model.Str(s.Store.Get().AuthUserEmail))
	if email == "" {
		email = strings.TrimSpace(typedEmail)
	}
	if msg := validateNewPassword(email, code, pw, confirm); msg != "" {
		return fail(msg)
	}
	ctx, cancel := context.WithTimeout(ctx, passwordTimeout)
	defer cancel()
	token, err := s.EnsureAccessToken(ctx)
	if err == nil && token == "" {
		return fail("登录已失效，请重新登录")
	}
	if err != nil {
		return s.failure(err, "修改密码失败", "修改密码已取消")
	}
	if err := s.Client.ChangePassword(ctx, token, code, pw); err != nil {
		return s.failure(err, "修改密码失败", "修改密码已取消")
	}
	s.afterPasswordUpdated(email)
	return Result{OK: true, Message: "密码已更新，请使用新密码重新登录"}
}

// afterPasswordUpdated is ClearAuthAfterPasswordUpdated: the old session is
// no good, the address stays for the next sign-in.
func (s *Service) afterPasswordUpdated(email string) {
	_ = s.Store.Update(func(m *model.Settings) {
		m.AuthUserEmail = &email
		syncclient.ClearAuthSession(m, false)
	})
}

// Subscription is what the cloud tab shows about the plan.
type Subscription struct {
	Status    string // 订阅至 2026-01-02, 已订阅, 未订阅, 未登录, 查询失败, 正在读取中...
	Tier      string
	Storage   string
	Retention string
	Limit     string // 正常 / 已超限（可能影响同步）
	OverLimit bool
}

// Loading is the placeholder while the server is asked.
func Loading() Subscription {
	l := "正在读取中..."
	return Subscription{Status: l, Tier: l, Storage: l, Retention: l, Limit: l}
}

// None is a plan with nothing to show: status says why.
func None(status string) Subscription {
	return Subscription{Status: status, Tier: "-", Storage: "-", Retention: "-", Limit: "-"}
}

// Subscription asks the server (RefreshSubscriptionAsync).
func (s *Service) Subscription(ctx context.Context) Subscription {
	token, err := s.EnsureAccessToken(ctx)
	if err != nil || token == "" {
		return None("未登录")
	}
	st, err := s.Client.SubscriptionStatus(ctx, token)
	if err != nil || st == nil {
		return None("查询失败")
	}
	if !st.Subscribed {
		return None("未订阅")
	}
	out := Subscription{Status: "已订阅"}
	if st.ExpiresAt != nil {
		if t, ok := parseTime(*st.ExpiresAt); ok {
			out.Status = "订阅至 " + t.Format("2006-01-02")
		}
	}
	out.Tier = "未知"
	if st.Tier != nil && strings.TrimSpace(*st.Tier) != "" {
		out.Tier = *st.Tier
	}
	out.Storage = formatStorage(st.StorageBytes, st.StorageLimitBytes)
	out.Retention = "未限制"
	if st.RetentionDays != nil && *st.RetentionDays > 0 {
		out.Retention = fmt.Sprintf("%d 天", *st.RetentionDays)
	}
	switch {
	case st.OverLimit != nil:
		out.OverLimit = *st.OverLimit
	case st.StorageBytes != nil && st.StorageLimitBytes != nil && *st.StorageLimitBytes > 0:
		out.OverLimit = *st.StorageBytes > *st.StorageLimitBytes
	}
	out.Limit = "正常"
	if out.OverLimit {
		out.Limit = "已超限（可能影响同步）"
	}
	return out
}

func parseTime(s string) (time.Time, bool) {
	for _, l := range []string{time.RFC3339Nano, "2006-01-02T15:04:05.9999999", "2006-01-02T15:04:05", "2006-01-02"} {
		if t, err := time.Parse(l, strings.TrimSpace(s)); err == nil {
			return t, true
		}
	}
	return time.Time{}, false
}

// Redeem exchanges a card code (RedeemCardAsync).
func (s *Service) Redeem(ctx context.Context, code string) Result {
	code = strings.TrimSpace(code)
	if code == "" {
		return fail("请输入卡密")
	}
	token, err := s.EnsureAccessToken(ctx)
	if err != nil || token == "" {
		return fail("请先登录")
	}
	res, err := s.Client.RedeemCard(ctx, token, code)
	if err != nil {
		return fail("兑换失败：" + err.Error())
	}
	if res == nil {
		return fail("兑换失败：网络错误")
	}
	if !res.Success {
		e := res.Error
		if e == "" {
			e = "未知错误"
		}
		if strings.HasPrefix(e, "{") || strings.HasPrefix(e, "[") {
			e = "兑换失败"
		}
		return fail(e)
	}
	return Result{OK: true, Message: "兑换成功"}
}

func formatStorage(used, limit *int64) string {
	u, l := "-", "-"
	if used != nil {
		u = formatBytes(*used)
	}
	if limit != nil && *limit > 0 {
		l = formatBytes(*limit)
	}
	return u + " / " + l
}

// formatBytes is FormatBytes: 1024-based, two decimals at most.
func formatBytes(n int64) string {
	if n < 0 {
		n = 0
	}
	units := []string{"B", "KB", "MB", "GB", "TB"}
	v := float64(n)
	i := 0
	for v >= 1024 && i < len(units)-1 {
		v /= 1024
		i++
	}
	s := fmt.Sprintf("%.2f", v)
	s = strings.TrimRight(strings.TrimRight(s, "0"), ".")
	return s + " " + units[i]
}

// ExplainAuth is ExplainAuthException: the server's English reasons in
// Chinese, anything else after the fallback.
func ExplainAuth(err error, fallback string) string {
	if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, context.Canceled) {
		return "请求超时或已取消，请稍后重试"
	}
	msg := strings.TrimSpace(err.Error())
	if msg == "" {
		return fallback
	}
	has := func(sub string) bool { return strings.Contains(strings.ToLower(msg), sub) }
	switch {
	case has("invalid email or password"):
		return "邮箱或密码不正确"
	case has("invalid email"):
		return "请输入有效邮箱"
	case has("password must be at least"):
		return "新密码至少 8 位"
	case has("invalid or expired code"):
		return "验证码错误或已过期"
	case has("too many code attempts"):
		return "验证码错误次数过多，请重新获取"
	case has("email service not configured"):
		return "邮件服务尚未配置，请联系管理员"
	case has("failed to send email"):
		return "验证码邮件发送失败，请稍后重试"
	case has("invalid request"):
		return "登录请求格式异常，请重启应用后重试"
	case has("missing") && has("accesstoken"):
		return "登录响应异常，请稍后重试"
	case has("timed out") || has("timeout"):
		return "登录请求超时，请检查网络后重试"
	}
	return fallback + "：" + msg
}
