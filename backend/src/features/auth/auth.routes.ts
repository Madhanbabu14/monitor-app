import { Router, Request, Response } from 'express';
import { authService } from './auth.service';
import { authenticate } from '../../middleware/auth.middleware';
import { AppError } from '../../middleware/error.middleware';
import { logger } from '../../utils/logger';

const router = Router();

router.post('/login', async (req: Request, res: Response) => {
  const { email, password } = req.body;
  if (!email || !password) throw new AppError(400, 'Email and password are required');
  const result = await authService.login(email, password);
  res.json({ status: 'success', data: result });
});

// Azure AD SSO
router.post('/sso', async (req: Request, res: Response) => {
  const { azureToken } = req.body;
  logger.info('[SSO] POST /auth/sso received', {
    hasToken: !!azureToken,
    tokenType: typeof azureToken,
    tokenLength: typeof azureToken === 'string' ? azureToken.length : 0,
  });
  if (!azureToken || typeof azureToken !== 'string') {
    throw new AppError(400, 'azureToken is required');
  }
  const result = await authService.ssoLogin(azureToken);
  logger.info('[SSO] POST /auth/sso success', { email: result.user.email });
  res.json({ status: 'success', data: result });
});

router.get('/me', authenticate, (req: Request, res: Response) => {
  res.json({ status: 'success', data: req.user });
});

export { router as authRoutes };
